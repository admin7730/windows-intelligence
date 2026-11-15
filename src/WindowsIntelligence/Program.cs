using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WindowsIntelligence;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--import-elevenlabs-credential" or "--import-minimax-credential")
        {
            var key = Console.In.ReadLine();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 500) { Environment.ExitCode = 2; return; }
            bool minimax = args[0] == "--import-minimax-credential";
            SecretStore.Set(minimax ? MiniMaxSpeech.CredentialId : ElevenLabsSpeech.CredentialId, key.Trim());
            if (minimax)
            {
                var settings = Settings.Load();
                settings.Tts = TtsMode.MiniMax;
                settings.Save();
            }
            return;
        }
        var activation = Copilot.FromLaunch(args);
        if (!activation.HasValue) return;
        using var instance = new SingleInstance(Copilot.GetAumid());
        if (!instance.IsPrimary)
        {
            if (activation == Activation.Background || instance.Forward(activation.Value)) return;
            if (!instance.TryTakeOver())
            {
                Environment.ExitCode = 1;
                if (activation == Activation.Open)
                    MessageBox.Show("无法连接到已运行的 Windows Intelligence。请从任务栏通知区域退出应用后重新打开。",
                        "Windows Intelligence", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new AppHost(app);
        _ = instance.ListenAsync(message => app.Dispatcher.InvokeAsync(() => host.Activate(message)));
        app.Startup += (_, _) => host.Activate(activation.Value);
        app.Run();
    }
}

internal sealed partial class AppHost : IDisposable
{
    private readonly HwndSource receiver;
    private CopilotGate gate = new();
    private readonly AssistantHotkey shortcut;
    private readonly Stopwatch ticks = Stopwatch.StartNew();
    private readonly DispatcherTimer tapTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(CopilotGate.DoubleTapWindow) };
    private int fastPath;

    public AppHost(Application app)
    {
        receiver = new HwndSource(new HwndSourceParameters("Windows Intelligence · Copilot activation")
        {
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x00000080 | 0x08000000,
            Width = 1, Height = 1, PositionX = -100, PositionY = -100
        });
        shortcut = new AssistantHotkey(receiver.Handle, input => Activate(input, fastPathOrigin: true, customOrigin: true));
        shortcut.Changed += () => { tapTimer.Stop(); gate = new(); };
        RegisterReceiver(receiver);
        Initialize(app);
        tapTimer.Tick += (_, _) =>
        {
            tapTimer.Stop();
            HandleActivation(Activation.Tap);
        };
    }

    private void RegisterReceiver(HwndSource source)
    {
        source.AddHook(HandleMessage);
        fastPath = Copilot.RegisterFastPath(source.Handle);
    }

    public void Activate(Activation input, bool fastPathOrigin = false, bool customOrigin = false)
    {
        if (input is Activation.Tap or Activation.Down or Activation.Up &&
            customOrigin != (shortcut.Active is not null)) return;
        if (input is Activation.Tap or Activation.Down && window?.IsRecordingShortcut == true) return;
        if (gate.Resolve(input, ticks.ElapsedMilliseconds, fastPathOrigin) is not { } action) return;
        if (action == Activation.Tap)
        {
            tapTimer.Stop();
            tapTimer.Start();
            return;
        }
        if (action is Activation.SilentTap or Activation.Down) tapTimer.Stop();
        HandleActivation(action);
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == AssistantHotkey.WindowMessage && shortcut.HandleMessage((int)wParam))
        {
            handled = true;
            return 0;
        }
        if (message != Copilot.WindowMessage) return 0;
        var activation = wParam.ToInt64() switch { 0 => Activation.Tap, 1 => Activation.Down, 2 => Activation.Up, _ => Activation.Open };
        if (activation != Activation.Open)
        {
            receiver.Dispatcher.InvokeAsync(() => Activate(activation, fastPathOrigin: true));
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        tapTimer.Stop();
        shortcut.Dispose();
        Shutdown();
        receiver.Dispose();
    }

    partial void Initialize(Application application);
    partial void HandleActivation(Activation input);
    partial void Shutdown();
}
