using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace WindowsIntelligence;

internal enum Mood { Idle, Listening, Thinking, Speaking, Screenshot, Error }

internal sealed class Overlay : IDisposable
{
    private readonly Settings settings;
    private readonly bool renderOrb;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly DispatcherTimer frameClock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
    private readonly Dictionary<string, Surface> targets = new();
    private long last, nextFrame, activated;
    private double speed, radius, edge, flash;
    private int slowFrames, qualityReduction;
    private float smoothEnergy;
    private string activeScreen = "";
    private bool live, suppressed, running;
    private System.Windows.Threading.DispatcherTimer? responseLinger;
    private Mood mood;
    public float Energy { get; set; }
    public bool Ready { get; private set; } = true;
    public string? Failure { get; private set; }
    public event Action<Screen, double, double, double>? Place;
    public event Action? DisplayChanged;
    public int Frames { get; private set; }
    internal ulong TransferBytes => targets.Values.Aggregate(0UL, (bytes, surface) => bytes + OverlayTransferBytes(surface.Handle));
    public string BackdropStatus => string.Join(", ", targets.Values.Select(surface => $"0x{OverlayBackdropStatus(surface.Handle):X8}"));
    internal nint WindowFor(Screen screen) => targets.TryGetValue(screen.DeviceName, out var surface) ? OverlayHwnd(surface.Handle) : 0;
    internal uint ShaderPixel(Screen screen, int x, int y) => targets.TryGetValue(screen.DeviceName, out var surface) ? OverlayPixel(surface.Handle, x, y) : 0;

    public Overlay(Settings settings, bool renderOrb = true)
    {
        this.settings = settings; this.renderOrb = renderOrb;
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

    public void SetActive(Screen screen, Mood state)
    {
        responseLinger?.Stop();
        bool appearing = !live;
        activeScreen = screen.DeviceName;
        live = true;
        mood = state;
        if (appearing)
        {
            activated = watch.ElapsedMilliseconds;
            nextFrame = activated + (settings.ReduceMotion || !SystemParameters.ClientAreaAnimation ? 0 : 105);
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

    public (double X, double Y, double Scale) Position(Screen screen)
    {
        var scale = targets.TryGetValue(screen.DeviceName, out var surface) ? surface.Scale : 1;
        double x = screen.Bounds.Width * (settings.OrbPosition == 3 ? .77 : .50);
        double y = settings.OrbPosition switch { 0 => 108 * scale, 1 => screen.Bounds.Height * .36, _ => screen.Bounds.Height * .64 };
        return (x, y, scale);
    }

    private void Animate()
    {
        if (running || !Ready || suppressed || !live && radius < .006 && edge < .006 && flash < .008) return;
        if (!settings.Edge && !renderOrb && edge < .006 && flash < .008) return;
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
        double goal = live && time >= nextFrame ? 1 : 0;
        double edgeGoal = live && settings.Edge ? 1 : 0;
        bool reduceMotion = settings.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        if (reduceMotion) { radius = goal; edge = edgeGoal; }
        else
        {
            Spring(ref radius, ref speed, goal, 146, 19, dt);
            edge += (edgeGoal - edge) * (1 - Math.Exp(-dt * 8.5));
        }
        flash *= Math.Exp(-dt * 5.7);
        smoothEnergy += (float)((Math.Clamp(Energy * settings.Sensitivity, 0, 1) - smoothEnergy) * (1 - Math.Exp(-dt * 15)));
        if (live && settings.Adaptive)
        {
            slowFrames = elapsed > .026 ? Math.Min(150, slowFrames + 2) : Math.Max(0, slowFrames - 1);
            qualityReduction = slowFrames > 95 ? 2 : slowFrames > 45 ? 1 : 0;
        }
        foreach (var surface in targets.Values)
        {
            bool active = surface.Screen.DeviceName == activeScreen;
            bool show = edge > .006 || flash > .008 || (active && renderOrb && radius > .006);
            surface.Show(show);
            var (x, y, scale) = Position(surface.Screen);
            if (active && (live || radius > .006)) Place?.Invoke(surface.Screen, x, y, scale);
            if (!show) continue;
            double motionTime = reduceMotion ? 0 : (time - activated) / 1000d * settings.AnimationSpeed;
            float orbRadius = (float)(Math.Max(0, radius) * settings.OrbSize * scale / 2 * (1 + .017 * Math.Sin(motionTime * 2.3)));
            var frame = new Scene
            {
                Width = surface.Screen.Bounds.Width, Height = surface.Screen.Bounds.Height, Time = (float)motionTime, Scale = (float)scale,
                OrbX = (float)x, OrbY = (float)y, Radius = active && renderOrb ? orbRadius : 0, OrbOpacity = active && renderOrb ? (float)Math.Clamp(radius * 2, 0, 1) : 0,
                Energy = smoothEnergy, Mood = (int)mood, Edge = (float)edge, Flash = (float)flash,
                Glass = (float)settings.Glass, Glow = (float)settings.Glow, Palette = (float)settings.Palette, Blur = (float)settings.Blur
            };
            int limit = Math.Max(0, (int)settings.Quality - qualityReduction) switch { 0 => 24, 1 => 72, 2 => 130, _ => 240 };
            int count = !settings.Particles ? 0 : active ? Math.Min(settings.ParticleCount, limit) : 0;
            var hr = DrawOverlay(surface.Handle, ref frame, count);
            if (hr == 0) Frames++;
            if (hr < 0) { Failure = $"GPU 叠加层呈现失败 (0x{hr:X8})。"; Ready = false; break; }
        }
        if (!Ready || (!live && radius < .006 && edge < .006 && flash < .008)) StopAnimating();
    }

    private void StopAnimating()
    {
        if (!running) return;
        running = false;
        frameClock.Stop();
    }

    private static void Spring(ref double position, ref double velocity, double goal, double stiffness, double damping, double dt)
    {
        velocity += ((goal - position) * stiffness - velocity * damping) * dt;
        position += velocity * dt;
    }

    public void Dispose()
    {
        responseLinger?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
        StopAnimating();
        foreach (var surface in targets.Values) surface.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Scene
    {
        public float Width, Height, Time, Scale, OrbX, OrbY, Radius, OrbOpacity, Energy, Mood, Edge, Flash;
        public float Blur, Glass, Glow, Palette;
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
    [DllImport("WinNative.dll")] private static extern int DrawOverlay(nint handle, ref Scene frame, int particles);
    [DllImport("WinNative.dll")] private static extern void ShowOverlay(nint handle, [MarshalAs(UnmanagedType.Bool)] bool show);
    [DllImport("WinNative.dll")] private static extern void DestroyOverlay(nint handle);
    [DllImport("WinNative.dll")] private static extern nint OverlayHwnd(nint handle);
    [DllImport("WinNative.dll")] private static extern int OverlayBackdropStatus(nint handle);
    [DllImport("WinNative.dll")] private static extern uint OverlayPixel(nint handle, int x, int y);
    [DllImport("WinNative.dll")] private static extern ulong OverlayTransferBytes(nint handle);
}
