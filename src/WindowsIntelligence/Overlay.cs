using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace WindowsIntelligence;

internal sealed class Overlay : IDisposable
{
    private readonly Settings settings;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly DispatcherTimer frameClock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
    private readonly Dictionary<string, Surface> targets = new();
    private long last, activated;
    private double edge, flash;
    private float smoothEnergy;
    private string activeScreen = "";
    private bool live, suppressed, running;
    private System.Windows.Threading.DispatcherTimer? responseLinger;
    public float Energy { get; set; }
    public bool Ready { get; private set; } = true;
    public string? Failure { get; private set; }
    public event Action<Screen, double>? Place;
    public event Action? DisplayChanged;
    public int Frames { get; private set; }
    internal ulong TransferBytes => targets.Values.Aggregate(0UL, (bytes, surface) => bytes + OverlayTransferBytes(surface.Handle));
    internal nint WindowFor(Screen screen) => targets.TryGetValue(screen.DeviceName, out var surface) ? OverlayHwnd(surface.Handle) : 0;
    internal uint ShaderPixel(Screen screen, int x, int y) => targets.TryGetValue(screen.DeviceName, out var surface) ? OverlayPixel(surface.Handle, x, y) : 0;

    public Overlay(Settings settings)
    {
        this.settings = settings;
        frameClock.Tick += Tick;
        RefreshDisplays();
        SystemEvents.DisplaySettingsChanged += OnDisplayChange;
    }

    private void OnDisplayChange(object? sender, EventArgs args) => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        RefreshDisplays();
        DisplayChanged?.Invoke();
    });

    private void RefreshDisplays()
    {
        foreach (var surface in targets.Values) surface.Dispose();
        targets.Clear();
        Ready = true;
        try { foreach (var screen in Screen.AllScreens) targets[screen.DeviceName] = new Surface(screen); }
        catch (Exception ex) { Ready = false; Failure = $"DirectComposition 初始化失败 (0x{ex.HResult:X8})。"; }
        if (live) Animate();
    }

    public void SetActive(Screen screen)
    {
        responseLinger?.Stop();
        bool appearing = !live;
        activeScreen = screen.DeviceName;
        live = true;
        if (appearing)
        {
            activated = watch.ElapsedMilliseconds;
        }
        Animate();
    }

    public void Pulse() { flash = 1; Animate(); }
    public void Dismiss() { responseLinger?.Stop(); live = false; Energy = 0; Animate(); }
    public void Settle(TimeSpan delay)
    {
        responseLinger ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
        responseLinger.Stop();
        responseLinger.Tick -= LingerFinished;
        responseLinger.Tick += LingerFinished;
        responseLinger.Interval = delay;
        responseLinger.Start();
    }

    private void LingerFinished(object? sender, EventArgs args) { responseLinger?.Stop(); Dismiss(); }
    public void Suppress(bool hide)
    {
        suppressed = hide;
        if (hide) { StopAnimating(); foreach (var surface in targets.Values) surface.Show(false); }
        if (!hide) Animate();
    }

    public double ScaleFor(Screen screen) => targets.TryGetValue(screen.DeviceName, out var surface) ? surface.Scale : 1;

    private void Animate()
    {
        if (running || !Ready || suppressed || !live && edge < .006 && flash < .008) return;
        if (!settings.Edge && edge < .006 && flash < .008) return;
        running = true;
        last = watch.ElapsedTicks;
        frameClock.Start();
    }

    private void Tick(object? sender, EventArgs e) => Draw();

    private void Draw()
    {
        if (suppressed || !Ready) return;
        var stamp = watch.ElapsedTicks;
        double elapsed = (double)(stamp - last) / Stopwatch.Frequency;
        double dt = Math.Clamp(elapsed, 0.001, .034);
        if (elapsed < .0025) return;
        last = stamp;
        var time = watch.ElapsedMilliseconds;
        double edgeGoal = live && settings.Edge ? 1 : 0;
        bool reduceMotion = settings.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        if (reduceMotion) edge = edgeGoal;
        else edge += (edgeGoal - edge) * (1 - Math.Exp(-dt * 8.5));
        flash *= Math.Exp(-dt * 5.7);
        smoothEnergy += (float)((Math.Clamp(Energy * settings.Sensitivity, 0, 1) - smoothEnergy) * (1 - Math.Exp(-dt * 15)));
        foreach (var surface in targets.Values)
        {
            bool active = surface.Screen.DeviceName == activeScreen;
            bool show = edge > .006 || flash > .008;
            surface.Show(show);
            double scale = surface.Scale;
            if (active && (live || show)) Place?.Invoke(surface.Screen, scale);
            if (!show) continue;
            double motionTime = reduceMotion ? 0 : (time - activated) / 1000d * settings.AnimationSpeed;
            var frame = new Scene
            {
                Width = surface.Screen.Bounds.Width, Height = surface.Screen.Bounds.Height, Time = (float)motionTime, Scale = (float)scale,
                Energy = smoothEnergy, Edge = (float)edge, Flash = (float)flash,
                Glow = (float)settings.Glow, Palette = (float)settings.Palette
            };
            var hr = DrawOverlay(surface.Handle, ref frame);
            if (hr == 0) Frames++;
            if (hr < 0) { Failure = $"GPU 叠加层呈现失败 (0x{hr:X8})。"; Ready = false; break; }
        }
        if (!Ready || (!live && edge < .006 && flash < .008)) StopAnimating();
    }

    private void StopAnimating()
    {
        if (!running) return;
        running = false;
        frameClock.Stop();
    }

    public void Dispose()
    {
        responseLinger?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
        StopAnimating();
        foreach (var surface in targets.Values) surface.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)] private struct Scene
    {
        public float Width, Height, Time, Scale;
        public float Energy, Edge, Flash, Glow;
        public float Palette;
    }

    private sealed class Surface : IDisposable
    {
        public Screen Screen { get; }
        public nint Handle { get; }
        public double Scale { get; }

        public Surface(Screen screen)
        {
            Screen = screen;
            var b = screen.Bounds;
            Marshal.ThrowExceptionForHR(CreateOverlay(b.Left, b.Top, b.Width, b.Height, out var handle));
            Handle = handle;
            Scale = Math.Max(96, Desktop.GetDpiForWindow(OverlayHwnd(handle))) / 96d;
        }

        public void Show(bool visible) => ShowOverlay(Handle, visible);
        public void Dispose() => DestroyOverlay(Handle);
    }

    [DllImport("WinNative.dll")] private static extern int CreateOverlay(int x, int y, int w, int h, out nint handle);
    [DllImport("WinNative.dll")] private static extern int DrawOverlay(nint handle, ref Scene frame);
    [DllImport("WinNative.dll")] private static extern void ShowOverlay(nint handle, [MarshalAs(UnmanagedType.Bool)] bool show);
    [DllImport("WinNative.dll")] private static extern void DestroyOverlay(nint handle);
    [DllImport("WinNative.dll")] private static extern nint OverlayHwnd(nint handle);
    [DllImport("WinNative.dll")] private static extern uint OverlayPixel(nint handle, int x, int y);
    [DllImport("WinNative.dll")] private static extern ulong OverlayTransferBytes(nint handle);
}
