using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace WindowsIntelligence;

internal sealed record AssistantShortcut(ModifierKeys Modifiers, int VirtualKey)
{
    public bool IsValid => ((uint)Modifiers & ~15u) == 0 &&
        (Modifiers != ModifierKeys.None || VirtualKey is >= 0x70 and <= 0x87) &&
        VirtualKey is >= 0x08 and <= 0xFE and not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x7B) &&
        VirtualKey is not (>= 0xA0 and <= 0xA5);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey(VirtualKey);
        parts.Add(VirtualKey is >= 0x30 and <= 0x39 ? (VirtualKey - 0x30).ToString() : key switch
        {
            Key.Return => "Enter",
            Key.Escape => "Esc",
            _ => key.ToString()
        });
        return string.Join(" + ", parts);
    }
}

internal sealed class AssistantHotkey : IDisposable
{
    internal const int WindowMessage = 0x0312;
    private const int FirstId = 0x4313, SecondId = 0x4314;
    private const uint NoRepeat = 0x4000;
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(420);

    private readonly nint receiver;
    private readonly Action<Activation> activate;
    private readonly DispatcherTimer poll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(20) };
    private int registeredId;
    private long down;
    private bool holding;
    public AssistantShortcut? Active { get; private set; }
    public event Action? Changed;

    public AssistantHotkey(nint receiver, Action<Activation> activate)
    {
        this.receiver = receiver;
        this.activate = activate;
        poll.Tick += (_, _) => TrackPress();
    }

    public void Apply(AssistantShortcut? shortcut, Action? persist = null)
    {
        if (Active == shortcut)
        {
            persist?.Invoke();
            return;
        }
        int nextId = 0;
        if (shortcut is not null)
        {
            if (!shortcut.IsValid) throw new UserError("请选择带 Ctrl、Alt、Shift 或 Win 的组合，或使用功能键。Windows 保留 F12。");
            nextId = registeredId == FirstId ? SecondId : FirstId;
            if (!RegisterHotKey(receiver, nextId, (uint)shortcut.Modifiers | NoRepeat, (uint)shortcut.VirtualKey))
            {
                int error = Marshal.GetLastWin32Error();
                throw new UserError(error == 1409
                    ? $"{shortcut} 已被占用，请换一个组合。"
                    : $"Windows 无法注册 {shortcut}（{new Win32Exception(error).Message}）。");
            }
        }
        try { persist?.Invoke(); }
        catch
        {
            if (nextId != 0) UnregisterHotKey(receiver, nextId);
            throw;
        }
        if (registeredId != 0) UnregisterHotKey(receiver, registeredId);
        poll.Stop();
        if (holding) activate(Activation.Up);
        holding = false;
        Active = shortcut;
        registeredId = nextId;
        Changed?.Invoke();
    }

    public bool HandleMessage(int id)
    {
        if (Active is null || id != registeredId) return false;
        if (poll.IsEnabled) return true;
        if (!IsDown(Active.VirtualKey))
        {
            activate(Activation.Tap);
            return true;
        }
        down = Stopwatch.GetTimestamp();
        poll.Start();
        return true;
    }

    private void TrackPress()
    {
        if (Active is null) { poll.Stop(); return; }
        if (!IsDown(Active.VirtualKey))
        {
            poll.Stop();
            bool releasedHold = holding;
            holding = false;
            activate(releasedHold ? Activation.Up : Activation.Tap);
            return;
        }
        if (!holding && Stopwatch.GetElapsedTime(down) >= HoldThreshold)
        {
            holding = true;
            activate(Activation.Down);
        }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        poll.Stop();
        if (registeredId != 0) UnregisterHotKey(receiver, registeredId);
        registeredId = 0;
        Active = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
