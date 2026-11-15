using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Screen = System.Windows.Forms.Screen;

namespace WindowsIntelligence;

internal static class OverlayUi
{
    public static Window Window(string name, bool focus = false)
    {
        var window = new Window
    {
        Title = name, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
        ShowInTaskbar = false, Topmost = true, ShowActivated = focus, ResizeMode = ResizeMode.NoResize,
        SizeToContent = SizeToContent.WidthAndHeight, UseLayoutRounding = true, FontFamily = Ui.Body
    };

        AsToolWindow(window);
        return window;
    }

    // WPF's taskbar setting alone does not exclude a window from Alt+Tab.
    public static void AsToolWindow(Window window)
    {
        window.ShowInTaskbar = false;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = GetWindowLongPtr(hwnd, -20).ToInt64();
            SetWindowLongPtr(hwnd, -20, (nint)((style | 0x80) & ~0x40000));
            Desktop.Exclude(window);
        };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    public static void Position(Window window, int x, int y, int width = 0, int height = 0)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Desktop.SetWindowPos(handle, -1, x, y, width, height, (uint)(0x10 | (width == 0 ? 1 : 0)));
    }
}

internal sealed class Caption : IDisposable
{
    private static readonly Brush Ink = Ui.Brush("#222534"), Quiet = Ui.Brush("#5F6478");
    private readonly Window window = OverlayUi.Window("Windows Intelligence · 助手");
    private readonly TextBlock phase = Ui.Text("", 12, Quiet, true), subtitle = Ui.Text("", 13, Ink);
    private readonly MarkdownReplyView response = new() { MaxHeight = 265, Light = true };
    private readonly TextBox prompt = new() { MinHeight = 26, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock placeholder = Ui.Text("输入问题", 14, Quiet);
    private readonly TextBlock privacy = Ui.Text("", 12, Quiet);
    private readonly Image thumbnail = new() { Width = 126, Height = 75, Stretch = Stretch.Uniform };
    private readonly Button send, read, retry, thumbnailButton, microphone;
    private readonly System.Windows.Shapes.Path actionGlyph = new()
    {
        StrokeThickness = 1.6, Stretch = Stretch.Uniform, Width = 15, Height = 17,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
    };
    private static readonly Geometry MicGlyph = Geometry.Parse("M 7,2 A 2,2 0 0 1 11,2 L 11,8 A 2,2 0 0 1 7,8 Z M 4.5,7 A 4.5,4.5 0 0 0 13.5,7 M 9,12 L 9,16 M 6,16 L 12,16");
    private static readonly Geometry SendGlyph = Geometry.Parse("M 8 14 L 8 2 M 3 7 L 8 2 L 13 7");
    private static readonly Geometry MuteGlyph = Geometry.Parse("M 2 5 L 5 5 L 9 2 L 9 14 L 5 11 L 2 11 Z M 12 6 L 16 10 M 16 6 L 12 10");
    private static readonly Brush ActionInk = Ui.Brush("#424B8E"), ActionFill = Ui.Brush("#DCE6FF"), SendFill = Ui.Brush("#4D5DC1");
    private readonly Border shell;
    private readonly Border capsule;
    private readonly ScaleTransform iconScale = new(1,1);
    private readonly ScaleTransform windowScale = new(1, 1);
    private readonly TranslateTransform windowSlide = new();
    private readonly StackPanel root = new() { Width = 406, Margin = new Thickness(9, 6, 9, 13), RenderTransformOrigin = new Point(.90, 0) };
    private readonly GradientStop tintStart = new((Color)ColorConverter.ConvertFromString("#FAFFFFFF"),0), tintEnd = new((Color)ColorConverter.ConvertFromString("#E9E6F1FF"),1);
    private Window? reader;
    private MarkdownReplyView? readerContent;
    private BitmapSource? image;
    private string markdown = "";
    private bool readyForFollowUp;
    private int lastX = int.MinValue, lastY = int.MinValue;
    public event Action<string>? Submit, FollowUp;
    public event Action? Cancel, Retry, OpenImage, Speak;
    public bool ReduceMotion { get; set; }
    public bool Silent { get; set; }
    private const int EscapeHotKey = 0x4312;
    private bool escapeRegistered, closing;
    private int transition;
    private string lastVisualState = "";

    public Caption()
    {
        var windowTransform = new TransformGroup();
        windowTransform.Children.Add(windowScale);
        windowTransform.Children.Add(windowSlide);
        root.RenderTransform = windowTransform;
        var bar = new Grid { Height = 46 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });

        var mark = new Grid { Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(.5, .5) };
        mark.RenderTransform = iconScale;
        var ring = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(229, 116, 108), 0),
            new(Color.FromRgb(213, 113, 145), .30),
            new(Color.FromRgb(157, 122, 207), .57),
            new(Color.FromRgb(98, 147, 216), .82),
            new(Color.FromRgb(105, 182, 201), 1)
        }, 48);
        ring.Freeze();
        mark.Children.Add(new Ellipse { Fill = Glass("#36FFFFFF", "#24CECBEC"), Stroke = ring, StrokeThickness = 1.45 });
        var thread = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1,3 C 5,1 8,3 10,6 C 12,8 13.5,9 16,8 M 1,10 C 4,10 6,9 8,6 C 10,3 13,2 16,3"),
            Stroke = ring, StrokeThickness = 1.18,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Fill, Margin = new Thickness(2.6, 4.9, 2.6, 4.9)
        };
        mark.Children.Add(thread);
        bar.Children.Add(mark);
        var input = new Grid();
        Grid.SetColumn(input, 1); bar.Children.Add(input);
        input.Children.Add(prompt);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.IsHitTestVisible = false;
        input.Children.Add(placeholder);
        prompt.FontSize = 15;
        prompt.Background = Brushes.Transparent;
        prompt.Foreground = Ink;
        prompt.CaretBrush = Ink;
        prompt.BorderThickness = new Thickness(0);
        prompt.Padding = new Thickness(0);
        prompt.TextChanged += (_, _) => UpdateAction();
        System.Windows.Automation.AutomationProperties.SetName(prompt, "输入问题");
        prompt.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || prompt.IsReadOnly) return;
            SendPrompt();
            e.Handled = true;
        };

        microphone = Ui.Button("输入操作", () =>
        {
            if (!string.IsNullOrWhiteSpace(prompt.Text)) SendPrompt();
            else if (!Silent) Speak?.Invoke();
        });
        microphone.Content = actionGlyph;
        microphone.Width = microphone.Height = 30;
        microphone.MinHeight = 0;
        microphone.Padding = new Thickness(0);
        microphone.Margin = new Thickness(0, 0, 3, 0);
        microphone.VerticalAlignment = VerticalAlignment.Center;
        microphone.BorderBrush = Brushes.Transparent;
        Grid.SetColumn(microphone, 2);
        bar.Children.Add(microphone);
        var tint = new LinearGradientBrush(new GradientStopCollection { tintStart, tintEnd }, 37);
        var glassRim = Glass("#FFFFFFFF", "#8CBEC8DD");
        capsule = new Border
        {
            Child = bar, CornerRadius = new CornerRadius(23), BorderThickness = new Thickness(1), BorderBrush = glassRim, Background = tint,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 1, Direction = 270, Opacity = .39,
                Color = Color.FromRgb(113, 112, 228), RenderingBias = RenderingBias.Performance }
        };
        root.Children.Add(capsule);

        var content = new StackPanel();
        shell = new Border
        {
            Child = content, CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1), Padding = new Thickness(17),
            BorderBrush = glassRim, Background = Glass("#FAFFFFFF", "#F2EDF0FA"), Margin = new Thickness(0, 8, 0, 0)
        };
        root.Children.Add(shell);
        window.Content = root;
        phase.Margin = new Thickness(0, 0, 0, 8);
        subtitle.Margin = new Thickness(0, 0, 0, 8);
        privacy.Margin = new Thickness(0, 4, 0, 8);
        response.Margin = new Thickness(0, 0, 0, 8);
        send = PanelButton("发送", () => Submit?.Invoke(prompt.Text), primary: true);
        retry = PanelButton("重试", () => Retry?.Invoke());
        read = PanelButton("展开回复", ShowReply);
        thumbnailButton = PanelButton("截图", () => OpenImage?.Invoke());
        thumbnailButton.Content = thumbnail;
        thumbnailButton.Visibility = Visibility.Collapsed;
        thumbnailButton.HorizontalAlignment = HorizontalAlignment.Left;
        thumbnailButton.Margin = new Thickness(0, 0, 0, 8);
        thumbnailButton.Padding = new Thickness(0);
        var footer = Ui.Row(PanelButton("关闭", () => Cancel?.Invoke()), retry, read, send);
        foreach (var item in new UIElement[] { phase, thumbnailButton, subtitle, response, privacy, footer })
            content.Children.Add(item);
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel?.Invoke(); e.Handled = true; } };
        window.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(window);
            source.AddHook(EscapeMessage);
        };
        window.SizeChanged += (_, _) => lastX = int.MinValue;
        State("", "", "", false, false, "");
    }

    public void State(string status, string input, string answer, bool canSend, bool canRetry, string warning, bool voicePending = false)
    {
        bool listening = status == "正在聆听";
        phase.Text = status;
        phase.Visibility = status.Length == 0 || status == "输入问题" || listening ? Visibility.Collapsed : Visibility.Visible;
        bool completed = status.StartsWith("回复完成", StringComparison.Ordinal);
        if (completed && !readyForFollowUp) prompt.Clear();
        readyForFollowUp = completed;
        placeholder.Text = listening ? "正在聆听…" : completed ? Silent ? "继续提问 · 静默" : "继续提问"
            : canSend || status.Length == 0 ? Silent ? "输入问题 · 静默" : "输入问题" : "正在处理…";
        prompt.IsReadOnly = !canSend && !completed;
        UpdateAction();
        subtitle.Text = input;
        subtitle.Visibility = string.IsNullOrWhiteSpace(input) ? Visibility.Collapsed : Visibility.Visible;
        markdown = answer;

        response.SetMarkdown(answer, completed);
        readerContent?.SetMarkdown(answer, completed);
        response.Visibility = answer.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        read.Visibility = answer.Length > 190 ? Visibility.Visible : Visibility.Collapsed;
        privacy.Text = warning;
        privacy.Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        send.Visibility = canSend && !voicePending && (image is not null || input.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        retry.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        var visibility = phase.Visibility == Visibility.Visible || subtitle.Visibility == Visibility.Visible ||
            answer.Length > 0 || warning.Length > 0 || thumbnailButton.Visibility == Visibility.Visible || canRetry ? Visibility.Visible : Visibility.Collapsed;
        if (visibility == Visibility.Visible && shell.Visibility != visibility && window.IsVisible)
            GlassMotion.Reveal(shell, ReduceMotion, -10, 245);
        shell.Visibility = visibility;
        string visual = listening ? "listen" : completed ? "done" : canRetry ? "error" : image is not null ? "vision"
            : canSend || status.Length == 0 ? Silent ? "silent" : "input" : "think";
        if (visual != lastVisualState)
        {
            lastVisualState = visual;
            var (top,bottom) = visual switch
            {
                "listen" => ("#F8FCF7FC", "#DBD3DCFF"), "think" => ("#F8F8F5FF", "#DAD5CFFF"),
                "vision" => ("#F8FBF4FD", "#DBD7E4FF"), "done" => ("#F9FBF9FF", "#E0E4E4FB"),
                "error" => ("#FAFFF5F7", "#E7FFDDE5"), "silent" => ("#F9FBF9FF", "#E4E1E1FB"),
                _ => ("#F8FBF8FF", "#DFDBDBF9")
            };
            SetTint(tintStart, top);
            SetTint(tintEnd, bottom);
            if (GlassMotion.Enabled(ReduceMotion))
            {
                iconScale.ScaleX = iconScale.ScaleY = .93;
                GlassMotion.Scale(iconScale, 1, 220);
            }
        }
    }

    private void SendPrompt()
    {
        if (readyForFollowUp) FollowUp?.Invoke(prompt.Text);
        else Submit?.Invoke(prompt.Text);
    }

    private void UpdateAction()
    {
        bool hasText = !string.IsNullOrWhiteSpace(prompt.Text);
        placeholder.Visibility = prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool canRun = !prompt.IsReadOnly && (hasText || !Silent && phase.Text != "正在聆听");
        string label = hasText ? "发送问题" : Silent ? "静默模式" : "开始语音输入";
        microphone.IsEnabled = canRun;
        actionGlyph.Data = hasText ? SendGlyph : Silent ? MuteGlyph : MicGlyph;
        actionGlyph.Stroke = hasText ? Brushes.White : ActionInk;
        microphone.Background = hasText ? SendFill : Silent ? ActionFill : Brushes.Transparent;
        microphone.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(microphone, label);
    }

    private nint EscapeMessage(nint hwnd, int message, nint id, nint key, ref bool handled)
    {
        if (message == 0x0312 && id == EscapeHotKey)
        {
            handled = true;
            Cancel?.Invoke();
        }
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);

    private void SetTint(GradientStop stop, string value)
    {
        var start = stop.Color;
        var end = (Color)ColorConverter.ConvertFromString(value);
        stop.Color = end;
        if (!GlassMotion.Enabled(ReduceMotion)) return;
        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(start, end, TimeSpan.FromMilliseconds(245)) { FillBehavior = FillBehavior.Stop });
    }

    private static LinearGradientBrush Glass(string top, string bottom) => new(
        (Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 35);

    private static Button PanelButton(string label, Action action, bool primary = false)
    {
        var button = Ui.Button(label, action);
        button.Background = Ui.Brush(primary ? "#007AFF" : "#36FFFFFF");
        button.Foreground = primary ? Brushes.White : Ink;
        button.BorderBrush = Brushes.Transparent;
        button.FontSize = 12;
        return button;
    }

    public void SetImage(BitmapSource? source, bool showPreview, bool animate = false)
    {
        image = source;

        thumbnail.Source = source;
        thumbnailButton.Visibility = image is not null && showPreview ? Visibility.Visible : Visibility.Collapsed;
        if (!animate || !showPreview || image is null) return;
        var transform = new TransformGroup();
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform();
        var slide = new TranslateTransform();
        transform.Children.Add(scale); transform.Children.Add(rotate); transform.Children.Add(slide);
        thumbnail.RenderTransformOrigin = new Point(.5, .5);
        thumbnail.RenderTransform = transform;
        var duration = new Duration(TimeSpan.FromMilliseconds(440));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.58, 1, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.58, 1, duration) { EasingFunction = easing });
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-12, 0, duration) { EasingFunction = easing });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-42, 0, duration) { EasingFunction = easing });
        thumbnail.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    public void ResetPrompt() => prompt.Clear();
    public void Show()
    {
        if (window.IsVisible && !closing) return;
        closing = false;
        transition++;
        bool visible = window.IsVisible;
        if (!visible) window.Show();
        // AllowsTransparency tool windows occasionally lose WS_VISIBLE after the first WPF show;
        // re-assert non-activating native visibility without stealing focus.
        Desktop.ShowNoActivate(window);
        escapeRegistered = RegisterHotKey(new WindowInteropHelper(window).Handle, EscapeHotKey, 0x4000, 0x1B);
        double opacity = visible ? root.Opacity : .42;
        double scale = visible ? windowScale.ScaleX : .972;
        double y = visible ? windowSlide.Y : -8;
        if (GlassMotion.Enabled(ReduceMotion)) Animate(opacity, 1, scale, 1, y, 0, 225);
        else ResetMotion();
    }
    public void FocusPrompt() { window.Activate(); prompt.Focus(); }
    public void Hide(bool animate = false)
    {
        reader?.Close();
        ReleaseEscape();
        if (!window.IsVisible) return;
        if (!animate || !GlassMotion.Enabled(ReduceMotion))
        {
            transition++;
            closing = false;
            window.Hide();
            ResetMotion();
            return;
        }
        if (closing) return;
        closing = true;
        int current = ++transition;
        Animate(root.Opacity, 0, windowScale.ScaleX, .98, windowSlide.Y, -6, 145, (_, _) =>
        {
            if (transition != current) return;
            closing = false;
            window.Hide();
            ResetMotion();
        });
    }

    private void Animate(double opacityFrom, double opacityTo, double scaleFrom, double scaleTo, double yFrom, double yTo,
        int milliseconds, EventHandler? complete = null)
    {
        DoubleAnimation Move(double from, double to) => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var opacity = Move(opacityFrom, opacityTo);
        if (complete is not null) opacity.Completed += complete;
        root.BeginAnimation(UIElement.OpacityProperty, opacity);
        windowScale.BeginAnimation(ScaleTransform.ScaleXProperty, Move(scaleFrom, scaleTo));
        windowScale.BeginAnimation(ScaleTransform.ScaleYProperty, Move(scaleFrom, scaleTo));
        windowSlide.BeginAnimation(TranslateTransform.YProperty, Move(yFrom, yTo));
    }

    private void ResetMotion()
    {
        root.BeginAnimation(UIElement.OpacityProperty, null);
        windowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        windowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        windowSlide.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void ReleaseEscape()
    {
        if (!escapeRegistered) return;
        UnregisterHotKey(new WindowInteropHelper(window).Handle, EscapeHotKey);
        escapeRegistered = false;
    }

    public void Place(Screen screen, double scale)
    {
        if (!window.IsVisible) return;
        var bounds = screen.WorkingArea;
        int top = bounds.Top + (int)Math.Round(23 * scale);
        int left = bounds.Right - (int)Math.Round(439 * scale);
        if (left != lastX || top != lastY)
        {
            lastX = left; lastY = top;
            OverlayUi.Position(window, left, top);
            Desktop.ShowNoActivate(window);
        }
    }

    private void ShowReply()
    {
        if (reader is not null) { reader.Activate(); return; }
        var view = new MarkdownReplyView { Padding = new Thickness(22), Background = Ui.Brush("#F8F5FC"), Light = true };
        view.SetMarkdown(markdown, immediate: true);
        readerContent = view;
        reader = new Window
        {
            Title = "Windows Intelligence · 回复", Width = 690, Height = 610,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = window,
            Content = view, Background = Ui.Brush("#F8F5FC"), Topmost = true
        };
        OverlayUi.AsToolWindow(reader);
        reader.Closed += (_, _) => { reader = null; readerContent = null; };
        reader.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Cancel?.Invoke(); e.Handled = true; } };
        reader.Show();
    }

    public void Dispose()
    {
        transition++;
        ReleaseEscape();
        window.Close();
    }
}

internal static class ImageEditor
{
    public static Task<BitmapSource?> EditAsync(BitmapSource source, Screen screen, bool initiallyCrop, CancellationToken ct)
    {
        var popup = new Window
        {
            Title = "Windows Intelligence · 截图预览", Background = Ui.Night, Foreground = Ui.Ink, Width = Math.Min(1000, screen.WorkingArea.Width * .85),
            Height = Math.Min(740, screen.WorkingArea.Height * .86), WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ShowInTaskbar = false
        };
        OverlayUi.AsToolWindow(popup);
        var frame = new DockPanel { Margin = new Thickness(15) };
        var canvas = new Grid { Background = Brushes.Black, ClipToBounds = true };
        var bitmap = new Image { Source = source, Stretch = Stretch.Uniform };
        var mask = new Canvas { IsHitTestVisible = false };
        var selection = new Rectangle { Stroke = Ui.Accent, StrokeThickness = 2, Fill = Ui.Brush("#328DBAFF"), Visibility = Visibility.Collapsed };
        mask.Children.Add(selection);
        canvas.Children.Add(bitmap); canvas.Children.Add(mask);
        var active = source;
        bool? redact = initiallyCrop ? false : null;
        var info = Ui.Text(initiallyCrop ? "拖动框选截图区域，再选择使用此图。" : "可框选裁剪或遮挡敏感区域。", 13, Ui.Muted);
        var actions = Ui.Row(Ui.Button("使用此图", () => { if (!initiallyCrop || !ReferenceEquals(active, source)) popup.DialogResult = true; }, true),
            Ui.Button("裁剪", () => { redact = false; info.Text = "拖动框选要保留的区域。"; }),
            Ui.Button("遮挡", () => { redact = true; info.Text = "拖动框选要遮挡的区域。"; }),
            Ui.Button("还原", () => { active = source; bitmap.Source = active; }), Ui.Button("返回", () => popup.DialogResult = false));
        var tools = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        tools.Children.Add(actions); tools.Children.Add(info);
        DockPanel.SetDock(tools, Dock.Top); frame.Children.Add(tools); frame.Children.Add(canvas); popup.Content = frame;
        Point? start = null;
        canvas.MouseLeftButtonDown += (_, e) => { if (redact is null) return; start = e.GetPosition(canvas); canvas.CaptureMouse(); };
        canvas.MouseMove += (_, e) =>
        {
            if (start is not { } origin) return;
            var rect = new Rect(origin, e.GetPosition(canvas));
            Canvas.SetLeft(selection, rect.Left); Canvas.SetTop(selection, rect.Top);
            selection.Width = rect.Width; selection.Height = rect.Height; selection.Visibility = Visibility.Visible;
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (start is not { } origin || redact is not { } mode) return;
            start = null; canvas.ReleaseMouseCapture(); selection.Visibility = Visibility.Collapsed;
            var rect = new Rect(origin, e.GetPosition(canvas));
            double s = Math.Min(canvas.ActualWidth / active.PixelWidth, canvas.ActualHeight / active.PixelHeight);
            if (s <= 0) return;
            double x = (canvas.ActualWidth - active.PixelWidth * s) / 2, y = (canvas.ActualHeight - active.PixelHeight * s) / 2;
            int left = Math.Clamp((int)((rect.Left - x) / s), 0, active.PixelWidth), right = Math.Clamp((int)Math.Ceiling((rect.Right - x) / s), 0, active.PixelWidth);
            int top = Math.Clamp((int)((rect.Top - y) / s), 0, active.PixelHeight), bottom = Math.Clamp((int)Math.Ceiling((rect.Bottom - y) / s), 0, active.PixelHeight);
            if (right - left < 2 || bottom - top < 2) return;
            active = ScreenCapture.Restrict(active, new Int32Rect(left, top, right - left, bottom - top), mode);
            bitmap.Source = active;
            info.Text = mode ? "已遮挡区域。选择使用此图以应用。" : "已裁剪。选择使用此图以应用。";
        };
        popup.KeyDown += (_, e) => { if (e.Key == Key.Escape) popup.DialogResult = false; };
        using var cancellation = ct.Register(() => popup.Dispatcher.InvokeAsync(() => { if (popup.IsVisible) popup.Close(); }));
        return Task.FromResult(!ct.IsCancellationRequested && popup.ShowDialog() == true ? active : null);
    }
}
