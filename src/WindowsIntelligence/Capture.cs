using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen = System.Windows.Forms.Screen;

namespace WindowsIntelligence;

internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public static PixelRect Of(System.Drawing.Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

internal static class Desktop
{
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(System.Drawing.Point point, uint flags);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint relationship);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out PixelRect rect);
    [DllImport("user32.dll")] public static extern int GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    public static Screen CursorScreen => Screen.FromPoint(System.Windows.Forms.Cursor.Position);
    public static nint Monitor(Screen screen) => MonitorFromPoint(new(screen.Bounds.Left + 1, screen.Bounds.Top + 1), 2);
    public static void ShowNoActivate(Window window) => ShowWindow(new WindowInteropHelper(window).Handle, 8);
}

internal sealed class AppWindowCaptureSuppression : IDisposable
{
    private const uint KeepPlacement = 0x0001 | 0x0002 | 0x0004 | 0x0010; // NOSIZE, NOMOVE, NOZORDER, NOACTIVATE
    private const uint ShowWindow = 0x0040, HideWindow = 0x0080;
    private readonly List<(Window Window, nint Handle)> hidden = [];

    public AppWindowCaptureSuppression()
    {
        foreach (Window window in Application.Current.Windows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (!window.IsVisible || handle == 0 || !Desktop.IsWindowVisible(handle)) continue;
            if (Desktop.SetWindowPos(handle, 0, 0, 0, 0, 0, KeepPlacement | HideWindow))
                hidden.Add((window, handle));
        }
    }

    public void Dispose()
    {
        foreach (var (window, handle) in hidden)
        {
            if (window.IsVisible && new WindowInteropHelper(window).Handle == handle)
                Desktop.SetWindowPos(handle, 0, 0, 0, 0, 0, KeepPlacement | ShowWindow);
        }
    }
}

internal sealed class ScreenCapture
{
    [DllImport("WinNative.dll")] private static extern int CaptureOne(nint target, [MarshalAs(UnmanagedType.Bool)] bool window, out nint pixels, out int width, out int height);

    public static async Task<BitmapSource> CaptureAsync(CaptureTarget target, Screen screen, nint foreground, CancellationToken ct)
    {
        if (target == CaptureTarget.ActiveWindow && (foreground == 0 || !Desktop.IsWindowVisible(foreground) || Desktop.IsIconic(foreground)))
            throw new UserError("当前没有可捕获的活动窗口。");
        var monitors = target == CaptureTarget.AllMonitors ? Screen.AllScreens : [target == CaptureTarget.PrimaryMonitor ? Screen.PrimaryScreen ?? screen : screen];
        try
        {
            return await Task.Run(async () =>
            {
                if (target == CaptureTarget.ActiveWindow) return Capture(foreground, true);
                if (monitors.Length == 1) return Capture(Desktop.Monitor(monitors[0]), false);
                var frames = await Task.WhenAll(monitors.Select(m => Task.Run(() => (m.Bounds, Frame: Capture(Desktop.Monitor(m), false)), ct))).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var bounds = monitors.Select(x => x.Bounds).Aggregate(System.Drawing.Rectangle.Union);
                if ((long)bounds.Width * bounds.Height > 110_000_000) throw new UserError("虚拟桌面过大，请选择单个屏幕或区域。");
                var combined = new WriteableBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null);
                foreach (var (rect, frame) in frames)
                {
                    int width = Math.Min(frame.PixelWidth, rect.Width), height = Math.Min(frame.PixelHeight, rect.Height);
                    var bytes = new byte[width * height * 4];
                    frame.CopyPixels(new Int32Rect(0, 0, width, height), bytes, width * 4, 0);
                    combined.WritePixels(new Int32Rect(rect.Left - bounds.Left, rect.Top - bounds.Top, width, height), bytes, width * 4, 0);
                }
                combined.Freeze();
                return combined;
            }, ct);
        }
        catch (COMException ex) { throw new UserError($"Windows Graphics Capture 无法取得画面 (0x{ex.HResult:X8})。请重试或更换截图目标。"); }
    }

    private static BitmapSource Capture(nint target, bool window)
    {
        Marshal.ThrowExceptionForHR(CaptureOne(target, window, out nint pixels, out int w, out int h));
        try
        {
            var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, checked(w * h * 4), w * 4);
            bitmap.Freeze();
            return bitmap;
        }
        finally { Marshal.FreeCoTaskMem(pixels); }
    }

    public static ImageInput Encode(BitmapSource image, string format, int quality, int maxSide)
    {
        double scale = Math.Min(1, (double)maxSide / Math.Max(image.PixelHeight, image.PixelWidth));
        if (scale < 1)
        {
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            image.Freeze();
        }
        bool jpeg = format == "JPEG";
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 50, 100) } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ImageInput(stream.ToArray(), jpeg ? "image/jpeg" : "image/png");
    }

    public static BitmapSource Restrict(BitmapSource image, Int32Rect region, bool redact)
    {
        if (!redact)
        {
            var crop = new CroppedBitmap(image, region);
            crop.Freeze();
            return crop;
        }
        var redacted = new WriteableBitmap(image);
        var pixels = new byte[region.Width * region.Height * 4];
        redacted.WritePixels(region, pixels, region.Width * 4, 0);
        redacted.Freeze();
        return redacted;
    }
}
