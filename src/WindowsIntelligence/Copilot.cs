using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel.AppExtensions;
using Windows.System;

namespace WindowsIntelligence;

internal enum Activation { Open, Tap, Down, Up, Background, SilentTap }

internal sealed class CopilotGate
{
    public const int DoubleTapWindow = 420;
    private long lastTap = -1000, lastRelease = -1000, lastDown = -100_000;
    private long lastDelivery = -1000;
    private bool lastFastPath;
    private bool held;

    public Activation? Resolve(Activation input, long now, bool fastPath = false)
    {
        switch (input)
        {
            case Activation.Open or Activation.Background: return input;
            case Activation.Tap:
                int duplicateInterval = fastPath == lastFastPath ? 85 : 170;
                if ((held && now - lastDown < 65_000) || now - lastDelivery < duplicateInterval || now - lastRelease < 90)
                    return null;
                held = false;
                lastDelivery = now;
                lastFastPath = fastPath;
                if (now - lastTap <= DoubleTapWindow)
                {
                    lastTap = -1000;
                    return Activation.SilentTap;
                }
                lastTap = now;
                return Activation.Tap;
            case Activation.Down:
                if (held && now - lastDown < 65_000) return null;
                held = true;
                lastDown = now;
                lastTap = -1000;
                return Activation.Down;
            case Activation.Up:
                if (!held) return null;
                held = false;
                lastRelease = now;
                return Activation.Up;
            default: return null;
        }
    }
}

internal static class Copilot
{
    public const int WindowMessage = 0x8001;
    public const string Provider = "com.microsoft.windows.copilotkeyprovider";
    public const string Scheme = "windowsintelligence-copilot";
    public const string SettingsUri = "ms-settings:personalization-textinput-copilot-hardwarekey";
    public const string PackageName = "WindowsIntelligence.Desktop";

    public static Activation? Parse(string value)
    {
        if (string.IsNullOrEmpty(value)) return Activation.Open;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Scheme || !string.IsNullOrEmpty(uri.Host)) return null;
        return uri.Query switch { "?state=Tap" => Activation.Tap, "?state=Down" => Activation.Down, "?state=Up" => Activation.Up, _ => null };
    }

    public static Activation? FromLaunch(string[] arguments)
    {
        if (arguments.Contains("--settings")) return Activation.Open;
        if (arguments.FirstOrDefault(arg => arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) is { } uri)
            return Parse(uri);
        try
        {
            if (GetAumid() is not null &&
                Windows.ApplicationModel.AppInstance.GetActivatedEventArgs() is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol &&
                protocol.Uri.Scheme == Scheme)
                return Parse(protocol.Uri.AbsoluteUri);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException) { }
        return Activation.Background;
    }

    public static string? GetAumid()
    {
        uint length = 0;
        if (GetCurrentApplicationUserModelId(ref length, null) != 122) return null;
        var builder = new StringBuilder((int)length);
        return GetCurrentApplicationUserModelId(ref length, builder) == 0 ? builder.ToString() : null;
    }

    public static void OpenSettings() => Process.Start(new ProcessStartInfo(SettingsUri) { UseShellExecute = true });

    public static async Task<string> DiagnoseAsync(int fastPath)
    {
        var aumid = GetAumid();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\BrandedKey");
        var choice = key?.GetValue("BrandedKeyChoiceType") as string;
        var current = key?.GetValue("AppAumid") as string;
        string extension = "当前进程没有 MSIX 包身份，请从开始菜单启动已安装的包。";
        string signature = "—";
        if (aumid is not null)
        {
            try
            {
                var package = Windows.ApplicationModel.Package.Current;
                signature = $"{package.SignatureKind} · {package.Id.Publisher}";
                if (package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.None)
                    signature += "\n此安装未通过选择器的签名条件；请安装受信任签名的 MSIX。";
                var entries = await AppExtensionCatalog.Open(Provider).FindAllAsync();
                extension = entries.Any(e => e.Package.Id.FamilyName == package.Id.FamilyName)
                    ? "Windows AppExtensionCatalog 已找到此包的 Copilot 扩展。"
                    : "Windows AppExtensionCatalog 未返回此包的扩展。请检查已安装包的清单及部署状态。";
            }
            catch (Exception ex) { extension = $"扩展目录查询失败 (0x{ex.HResult:X8})。"; }
        }
        string protocol;
        try { protocol = (await Launcher.QueryUriSupportAsync(new Uri($"{Scheme}://?state=Tap"), LaunchQuerySupportType.Uri)).ToString(); }
        catch (Exception ex) { protocol = $"0x{ex.HResult:X8}"; }
        string mapping = choice switch
        {
            "AppEnforcedByPolicy" => $"管理员策略指定 {current}",
            "App" when aumid is not null && current == aumid => "当前已映射到 Windows Intelligence",
            "App" => $"当前已映射到 {current}",
            "Search" => "当前映射到 Windows 搜索",
            _ => "系统未提供当前映射"
        };
        return $"Windows Build {Environment.OSVersion.Version.Build}\nAUMID  {aumid ?? "无"}\n签名  {signature}\n扩展  {extension}\n协议  {protocol}\n快速路径  0x{fastPath:X8}\n映射  {mapping}";
    }

    public static int RegisterFastPath(nint hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        int hr = SHGetPropertyStoreForWindow(hwnd, in iid, out var store);
        if (hr < 0) return hr;
        try
        {
            var key = new PropertyKey(new Guid("38652BCA-4329-4E74-86F9-39CF29345EEA"), 2);
            var value = new PropertyValue { Type = 23, UInt = WindowMessage };
            hr = store.SetValue(in key, in value);
            return hr < 0 ? hr : store.Commit();
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] private readonly record struct PropertyKey(Guid Id, uint Pid);
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropertyValue value);
        [PreserveSig] int SetValue(in PropertyKey key, in PropertyValue value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid iid, out IPropertyStore value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentApplicationUserModelId(ref uint length, StringBuilder? buffer);
}

internal sealed class SingleInstance : IDisposable
{
    private readonly string pipeName = $"WindowsIntelligence-{Process.GetCurrentProcess().SessionId}-{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value}";
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stopping = new();
    public bool IsPrimary { get; }

    public SingleInstance()
    {
        mutex = new Mutex(true, $@"Local\{pipeName}", out bool primary);
        IsPrimary = primary;
    }

    public void Forward(Activation activation)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        try { pipe.Connect(2500); pipe.WriteByte((byte)activation); }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    public async Task ListenAsync(Action<Activation> dispatch)
    {
        while (!stopping.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                read.CancelAfter(800);
                var data = new byte[1];
                if (await server.ReadAsync(data, read.Token).ConfigureAwait(false) == 1 && data[0] <= (byte)Activation.Background)
                    dispatch((Activation)data[0]);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
