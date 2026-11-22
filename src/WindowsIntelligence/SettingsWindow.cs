using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using InputKey = System.Windows.Input.Key;
using System.Windows.Media;
using System.Windows.Shell;
using Windows.ApplicationModel;

namespace WindowsIntelligence;

internal sealed class SettingsWindow : Window
{
    private static readonly Brush Canvas = Theme("#F2F4F8", SystemColors.WindowBrush), Rail = Theme("#56FFFFFF", SystemColors.WindowBrush), Surface = Theme("#CCFFFFFF", SystemColors.WindowBrush);
    private static readonly Brush Selected = Theme("#DCE9FF", SystemColors.HighlightBrush), Line = Theme("#1A263858", SystemColors.WindowTextBrush), Ink = Theme("#222838", SystemColors.WindowTextBrush);
    private static readonly Brush Muted = Theme("#657084", SystemColors.WindowTextBrush), Accent = Theme("#0869E8", SystemColors.HighlightBrush);
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private readonly Settings model;
    private readonly Interaction interaction;
    private readonly int fastPath;
    private readonly AssistantHotkey? shortcut;
    private readonly Overlay overlay;
    private readonly TextBlock status = Ui.Text("", 13, Accent);
    private readonly StackPanel page = new();
    private readonly List<Action> save = [];
    private readonly ListBox list = new();
    private readonly ScrollViewer scroll;
    private readonly TextBlock title = Ui.Text("", 25, Ink, true);
    private readonly TextBlock group = Ui.Text("", 12, Muted, true);
    private readonly TextBlock subtitle = Ui.Text("", 13, Muted);
    private CancellationTokenSource? microphoneTest;
    private AssistantShortcut? shortcutDraft;
    internal bool IsRecordingShortcut { get; private set; }
    private int tab;
    private static readonly string[] Pages = ["总览", "屏幕边缘", "唤醒快捷键", "截图", "语音识别", "模型服务", "视觉输入", "语音朗读", "性能", "隐私与历史", "关于", "小爱同学", "QQ 消息与文件", "对话与记忆", "任务派发"];
    private static readonly string[] Icons = ["\uE80F", "\uE7F4", "\uE765", "\uE722", "\uE720",
        "\uE968", "\uE91B", "\uE767", "\uE9D9", "\uE72E", "\uE946", "\uE767", "\uE8F2", "\uE8F2", "\uE724"];
    private static readonly string[] IconColors = ["#737EEB", "#B56ADB", "#8593A6", "#678BE8", "#EC6E8C", "#638CE8",
        "#AB78D5", "#ED9B58", "#54AFA5", "#6C9BC1", "#8993A3", "#EB995B", "#648CF0", "#8670C4", "#648CE8"];
    private static readonly string[] Descriptions = ["为你的声音、屏幕与日常操作，设置更顺手的体验。", "让每次唤醒，都有恰到好处的光。",
        "选择最顺手的方式，随时唤醒助手。", "让助手看见你想询问的内容。", "调整输入设备，获得清晰准确的转写。",
        "连接你信任的 AI 服务。", "设置助手如何理解屏幕与图片。", "选择自然的声音和朗读方式。", "在响应速度与资源占用之间，找到适合你的设置。",
        "管理对话记录与数据的保留方式。", "关于你的 Windows Intelligence。", "让小爱同学连接这台电脑，保留原有语音体验。", "在你允许的范围内，查看 QQ 通知和接收文件的动态。", "分别管理每段对话，以及由你定义的个人记忆和指令。", "把任务交给指定工具，在你选择的工作空间执行。"];
    private static readonly (string Name, int[] Pages)[] Sections =
    [
        ("", [0]),
        ("交互", [14, 4, 7, 2, 3, 11, 12]),
        ("服务与隐私", [13, 5, 6, 9]),
        ("外观与性能", [1, 8]),
        ("", [10])
    ];

    public SettingsWindow(Settings model, Interaction interaction, Overlay overlay, int fastPath, AssistantHotkey? shortcut = null)
    {
        this.model = model; this.interaction = interaction; this.overlay = overlay; this.fastPath = fastPath;
        this.shortcut = shortcut;
        Title = "Windows Intelligence · 设置"; Width = 1040; Height = 790; MinWidth = 820; MinHeight = 560;
        FontFamily = Ui.Body; FontSize = 14;
        Background = Canvas; Foreground = Ink; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1), CornerRadius = new CornerRadius(20), UseAeroCaptionButtons = false });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/WindowsIntelligence;component/SettingsTheme.xaml", UriKind.Relative)
        });
        Resources["SettingsCanvas"] = Canvas; Resources["SettingsSurface"] = Surface; Resources["SettingsSelected"] = Selected;
        Resources["SettingsLine"] = Line; Resources["SettingsInk"] = Ink; Resources["SettingsMuted"] = Muted;
        Resources["SettingsAccent"] = Accent;
        if (SystemParameters.HighContrast) ApplyHighContrast();
        SourceInitialized += (_, _) => ApplyBackdrop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            microphoneTest?.Cancel();
            IsRecordingShortcut = false;
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var side = new DockPanel { LastChildFill = true };
        var windowControls = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Margin = new Thickness(22, 0, 0, 0) };
        windowControls.Children.Add(WindowButton("关闭设置", "×", "#FF6059", Close));
        windowControls.Children.Add(WindowButton("最小化设置", "−", "#FFBD2E", () => WindowState = WindowState.Minimized));
        windowControls.Children.Add(WindowButton("切换最大化", "+", "#28C840", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        DockPanel.SetDock(windowControls, Dock.Top); side.Children.Add(windowControls);
        var brandLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        brandLabel.Children.Add(Ui.Text("Windows Intelligence", 15, Ink, true));
        brandLabel.Children.Add(Ui.Text("个人助理设置", 11, Muted));
        var mark = NavGlyph("\uE713", 22);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Foreground = Brushes.White;
        var brand = Ui.Row(new Border { Child = mark, Width = 42, Height = 42, Background = GlassFill("#AEB9CB", "#77869E"),
            BorderBrush = Ui.Brush("#D9FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0) }, brandLabel);
        brand.Margin = new Thickness(18, 8, 8, 22);
        DockPanel.SetDock(brand, Dock.Top); side.Children.Add(brand);
        var search = new TextBox { MinHeight = 34, Padding = new Thickness(10, 6, 8, 6), ToolTip = "搜索设置", Margin = new Thickness(14, 0, 14, 16) };
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索设置");
        search.Style = (Style)FindResource("SettingsSearch");
        search.Tag = L10n.T("搜索设置");
        WindowChrome.SetIsHitTestVisibleInChrome(search, true);
        DockPanel.SetDock(search, Dock.Top); side.Children.Add(search);
        list.Background = Brushes.Transparent; list.BorderThickness = new Thickness(0); list.Padding = new Thickness(10, 0, 10, 12);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        foreach (var section in Sections)
        {
            if (section.Name.Length > 0)
            {
                var label = Ui.Text(section.Name, 11, Muted, true);
                var divider = new ListBoxItem { Content = label, Focusable = false, IsHitTestVisible = false,
                    Padding = new Thickness(12, 14, 8, 4) };
                divider.Style = (Style)FindResource("SettingsNavItem");
                list.Items.Add(divider);
            }
            foreach (int index in section.Pages)
            {
                var glyph = NavGlyph(Icons[index], 14);
                glyph.Foreground = Brushes.White;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                var tile = new Border { Child = glyph, Width = 25, Height = 25, CornerRadius = new CornerRadius(7),
                    Background = Ui.Brush(IconColors[index]), Margin = new Thickness(0, 0, 10, 0) };
                var label = new TextBlock { Text = L10n.T(Pages[index]), VerticalAlignment = VerticalAlignment.Center };
                var item = new ListBoxItem { Tag = index, Content = Ui.Row(tile, label), Style = (Style)FindResource("SettingsNavItem") };
                System.Windows.Automation.AutomationProperties.SetName(item, Pages[index]);
                list.Items.Add(item);
            }
        }
        search.TextChanged += (_, _) =>
        {
            string query = search.Text.Trim();
            foreach (var item in list.Items.OfType<ListBoxItem>())
                item.Visibility = item.Tag is int index
                    ? (query.Length == 0 || Pages[index].Contains(query, StringComparison.CurrentCultureIgnoreCase) || L10n.T(Pages[index]).Contains(query, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed)
                    : (query.Length == 0 ? Visibility.Visible : Visibility.Collapsed);
        };
        side.Children.Add(list);
        var sideGlass = new Border { Child = side, Background = GlassFill("#A6FFFFFF", "#64E4EAF8"), BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = Ui.Brush("#80FFFFFF") };
        if (SystemParameters.HighContrast) sideGlass.Background = SystemColors.WindowBrush;
        layout.Children.Add(sideGlass);
        var main = new DockPanel { Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : GlassFill("#DDF8FAFF", "#C8EDF0F8") };
        Grid.SetColumn(main, 1); layout.Children.Add(main);
        title.FontFamily = Ui.Display;
        var header = new StackPanel { Margin = new Thickness(30, 23, 30, 24) };
        group.Margin = new Thickness(0, 0, 0, 6);
        header.Children.Add(group);
        header.Children.Add(title);
        subtitle.Margin = new Thickness(0, 8, 0, 0); header.Children.Add(subtitle);
        DockPanel.SetDock(header, Dock.Top); main.Children.Add(header);
        var footer = new DockPanel { Margin = new Thickness(18, 12, 10, 12) };
        var saveButton = ActionButton("保存设置", () => Save(), true);
        DockPanel.SetDock(saveButton, Dock.Right); footer.Children.Add(saveButton);
        status.VerticalAlignment = VerticalAlignment.Center; status.Margin = new Thickness(0, 0, 12, 0); status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(status);
        var footerBorder = new Border { Child = footer, Background = Surface, BorderBrush = Ui.Brush("#EFFFFFFF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20), Margin = new Thickness(26, 10, 26, 18) };
        DockPanel.SetDock(footerBorder, Dock.Bottom); main.Children.Add(footerBorder);
        page.Margin = new Thickness(30, 1, 30, 8);
        scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        main.Children.Add(scroll);
        Content = layout;
        if (SystemParameters.HighContrast) Foreground = SystemColors.WindowTextBrush;
        OverlayUi.AsToolWindow(this);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: int index }) return;
            microphoneTest?.Cancel();
            tab = index;
            Render();
            scroll.ScrollToTop();
            GlassMotion.Reveal(page, model.ReduceMotion, 4, 145);
        };
        Navigate(0);
        Loaded += (_, _) => L10n.Apply(this);
    }

    private void RecognitionPage()
    {
        var devices = new List<(string Id, string Name)> { ("", "Windows 默认输入设备") };
        devices.AddRange(SpeechRecognizer.Microphones());
        int index = devices.FindIndex(device => device.Id == model.MicrophoneDeviceId);
        if (index < 0)
        {
            index = devices.Count;
            devices.Add((model.MicrophoneDeviceId, "已保存的设备暂不可用"));
        }
        Choose("麦克风", devices.Select(device => device.Name).ToArray(), index, value => model.MicrophoneDeviceId = devices[value].Id);
        Check("唤醒后开启麦克风", model.MicrophoneEnabled, value => model.MicrophoneEnabled = value);
        Check("高准确率复核", model.AccurateSpeech, value => model.AccurateSpeech = value);
        Add(Ui.Text(SpeechRecognizer.HasAccurateModel
            ? "使用 Qwen3-ASR 核对完整发言，支持普通话、粤语、英语及中英混说。关闭后使用响应更快的 SenseVoice。"
            : "当前使用 SenseVoice 复核。安装新版完整安装包后可使用 Qwen3-ASR。", 12, Muted));
        Number("说话结束判定（毫秒）", Math.Max(1500, model.Silence), 1500, 4000, value => model.Silence = (int)value);

        var meter = new UniformGrid { Rows = 1, Columns = 20, Margin = new Thickness(0, 3, 0, 12) };
        var bars = Enumerable.Range(0, 20).Select(_ => new Border
        {
            Height = 7, CornerRadius = new CornerRadius(2), Background = Line, Margin = new Thickness(0, 0, 3, 0)
        }).ToArray();
        foreach (var bar in bars) meter.Children.Add(bar);
        var result = Ui.Text("说一句平时会用的指令，听完后这里会显示最终转写。", 13, Muted);
        result.MinHeight = 36;
        var start = ActionButton("开始试录", primary: true);
        var stop = ActionButton("停止", () => microphoneTest?.Cancel());
        start.Click += (_, _) => Run(Test());
        stop.IsEnabled = false;
        var content = new StackPanel();
        content.Children.Add(meter);
        content.Children.Add(result);
        content.Children.Add(Ui.Row(start, stop));
        Card("试录麦克风", "试录仅在本机转写。识别出的文字留在此处。", content);

        void Level(float value)
        {
            int on = (int)Math.Ceiling(value * bars.Length);
            for (int i = 0; i < bars.Length; i++) bars[i].Background = i < on ? Accent : Line;
        }

        async Task Test()
        {
            if (microphoneTest is not null || !Save()) return;
            if (!model.MicrophoneEnabled) { result.Text = "请先开启麦克风。"; return; }
            using var test = microphoneTest = new CancellationTokenSource();
            start.IsEnabled = false; stop.IsEnabled = true;
            result.Text = "正在聆听…";
            void Show(Action update) => Dispatcher.InvokeAsync(() => { if (!test.IsCancellationRequested) update(); });
            try
            {
                string text = await interaction.PreviewRecognitionAsync(value => Show(() => Level(value)),
                    text => Show(() => result.Text = text), () => Show(() => result.Text = "正在核对识别结果…"), test.Token);
                result.Text = text.Length > 0 ? text : "没有检测到说话声。请检查输入设备和音量后重试。";
            }
            catch (OperationCanceledException) when (test.IsCancellationRequested) { result.Text = "试录已停止。"; }
            catch (Exception ex) { result.Text = ex is UserError ? ex.Message : "无法完成试录。请检查 Windows 输入设备和麦克风权限。"; }
            finally
            {
                Level(0);
                start.IsEnabled = true; stop.IsEnabled = false;
                microphoneTest = null;
            }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private static Brush Theme(string color, Brush accessible) => SystemParameters.HighContrast ? accessible : Ui.Brush(color);

    private static LinearGradientBrush GlassFill(string top, string bottom)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 100);
        brush.Freeze(); return brush;
    }

    private void ApplyHighContrast()
    {
        Resources["SettingsSelectedInk"] = SystemColors.HighlightTextBrush;
        Resources["SettingsControl"] = SystemColors.WindowBrush;
        Resources["SettingsRim"] = SystemColors.WindowTextBrush;
    }

    private void ApplyBackdrop()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int light = 0, round = 2, mica = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref light, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        // DWM composites the backdrop; keep WPF hardware rendering and an opaque fallback.
        if (!SystemParameters.HighContrast && DwmSetWindowAttribute(handle, 38, ref mica, sizeof(int)) >= 0 &&
            HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
        }
    }

    private Button WindowButton(string name, string symbol, string color, Action action)
    {
        var button = new Button { Content = symbol, Width = 13, Height = 13, FontSize = 12,
            Background = Theme(color, SystemColors.ControlBrush), Foreground = Theme("#70222222", SystemColors.ControlTextBrush),
            Margin = new Thickness(0, 0, 9, 0), ToolTip = name, Style = (Style)FindResource("SettingsTrafficLight") };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action();
        return button;
    }

    private void Navigate(int index)
    {
        var item = list.Items.OfType<ListBoxItem>().Single(entry => entry.Tag is int pageIndex && pageIndex == index);
        list.SelectedItem = item;
        list.ScrollIntoView(item);
    }

    public void ShowShortcutSettings() => Navigate(2);

    private static TextBlock NavGlyph(string code, int size) =>
        new() { Text = code, FontFamily = GlyphFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private static Button ActionButton(string title, Action? action = null, bool primary = false)
    {
        var button = new Button { Content = L10n.T(title), Padding = new Thickness(16, 8, 16, 8), MinHeight = 35,
            Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, FontWeight = FontWeights.Medium };
        button.SetResourceReference(StyleProperty, "SettingsButton");
        button.Background = primary ? Accent : Rail;
        button.BorderBrush = primary ? Accent : Line;
        button.Foreground = primary ? (SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.White) : Ink;
        button.Click += (_, _) => action?.Invoke();
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        return button;
    }

    private void Render()
    {
        IsRecordingShortcut = false;
        status.Text = L10n.T("更改后保存以应用。"); save.Clear(); page.Children.Clear();
        string section = Sections.Single(entry => entry.Pages.Contains(tab)).Name;
        group.Text = L10n.T(section.Length > 0 ? section : "Windows Intelligence");
        title.Text = L10n.T(Pages[tab]);
        subtitle.Text = L10n.T(Descriptions[tab]);
        switch (tab)
        {
            case 0:
                var language = new ComboBox { ItemsSource = new[] { "简体中文", "繁體中文", "English" }, SelectedIndex = (int)model.Language, Width = 248 };
                SettingRow("界面与回复语言", language); save.Add(() => model.Language = (AppLanguage)language.SelectedIndex);
                Add(Ui.Text("保存后应用语言；原始提问、历史文本和代码不会被改写。",12,Muted));
                string wakeKey = shortcut?.Active?.ToString() ?? "Copilot 键";
                Card("语音与屏幕助手", L10n.F("短按 {0} 说话，说完后自动提交。双击静默输入，长按截屏。", "Tap {0} to speak. Double-tap for silent input; hold for a screenshot.", L10n.T(wakeKey)),
                    Ui.Row(ActionButton("开始语音", () => { Hide(); interaction.Start(false); }, true),
                        ActionButton("输入问题", () => { Hide(); interaction.Start(false, typed: true); }),
                        ActionButton("截图提问", () => { Hide(); Dispatcher.InvokeAsync(() => interaction.Start(true)); })));
                Card("当前服务", $"{model.Provider.Name}  ·  {model.Provider.TextModel}", ActionButton("配置模型服务", () => Navigate(5)));
                Label("操作电脑");
                Check("允许助手自动操作电脑", model.PowerShellEnabled, v => model.PowerShellEnabled = v);
                Add(Ui.Text("在语音或文字对话中提出操作请求后，助手会直接使用当前 Windows 账户执行。命令输出会发给当前 AI 服务。", 12, Muted));
                Card("启动与托盘", "登录 Windows 后保持 Copilot 键可用。",
                    Ui.Row(ActionButton("启用开机启动", () => Run(SetStartup(true))), ActionButton("关闭开机启动", () => Run(SetStartup(false)))));
                Check("显示系统托盘图标（下次启动）", model.Tray, v => model.Tray = v);
                Card("唤醒快捷键", L10n.F("当前使用 {0}。", "Currently using {0}.", L10n.T(wakeKey)), ActionButton("更改快捷键", ShowShortcutSettings));
                break;
            case 1:
                Check("唤醒时显示屏幕边缘光效", model.Edge, v => model.Edge = v);
                Number("发光强度", model.Glow, 0, 1.6, v => model.Glow = v);
                Number("音频灵敏度", model.Sensitivity, .2, 3, v => model.Sensitivity = v);
                Number("动画速度", model.AnimationSpeed, .25, 2.5, v => model.AnimationSpeed = v);
                Number("渐变色偏移", model.Palette, 0, 1, v => model.Palette = v);
                break;
            case 2: CopilotPage(); break;
            case 3:
                Choose("截图目标", new[] { "鼠标所在显示器", "主显示器", "所有显示器", "活动窗口", "手动选择区域" }, (int)model.Target, v => model.Target = (CaptureTarget)v);
                Number("松开后等待语音 (ms)", model.VoiceWait, 300, 10000, v => model.VoiceWait = (int)v);
                Check("松开键时立即结束录音", model.Strict, v => model.Strict = v);
                Check("显示截图缩略图", model.Preview, v => model.Preview = v);
                Multiline("默认截图提示词", model.ScreenshotPrompt, v => model.ScreenshotPrompt = v);
                break;
            case 4: RecognitionPage(); break;
            case 5: AiPage(); break;
            case 6:
                Field("视觉模型", model.Provider.VisionModel, v => model.Provider.VisionModel = v);
                Check("所选视觉模型支持图像", model.Provider.SupportsVision, v => model.Provider.SupportsVision = v);
                Choose("图像格式", ["PNG", "JPEG"], model.ImageFormat == "JPEG" ? 1 : 0, v => model.ImageFormat = v == 1 ? "JPEG" : "PNG");
                var details = new[] { "original", "high", "auto", "low" };
                Choose("视觉细节等级", details, Math.Max(0, Array.IndexOf(details, model.ImageDetail)), v => model.ImageDetail = details[v]);
                Number("JPEG 质量", model.JpegQuality, 50, 100, v => model.JpegQuality = (int)v);
                Number("图像长边上限 (px)", model.MaxImageSide, 768, 16000, v => model.MaxImageSide = (int)v);
                break;
            case 7: TtsPage(); break;
            case 8:
                Add(Ui.Text("语音键触发时后台加载准确识别模型；说完 30 秒后释放大模型，保留实时识别。使用中的识别和合成会先安全完成。",12,Muted));
                Check("减少动画", model.ReduceMotion, v => model.ReduceMotion = v);
                Add(Ui.Text(overlay.Failure ?? "Direct3D 11 · DirectComposition", 14, Muted));
                break;
            case 9: PrivacyPage(); break;
            case 10:
                Card($"Windows Intelligence {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}", "Windows 11 x64 · WPF · Direct3D 11 · Windows Graphics Capture · sherpa-onnx", Ui.Text(Copilot.GetAumid() ?? "本地调试进程", 13, Muted));
                break;
            case 11: XiaoAIPage(); break;
            case 12: QqPage(); break;
            case 13: MemoryPage(); break;
            case 14: DispatchPage(); break;
        }
    }

    private void MemoryPage()
    {
        Card("分开的对话，连续的交流", "应用启动先开新对话；默认在上次交互后 15 分钟内自动续接，超过后新建。也可随时手动新建或选择历史继续。",
            Ui.Row(ActionButton("查看历史对话", () => interaction.ShowConversations()), ActionButton("开启新对话", () => { interaction.NewConversation(); Hide(); }, true)));
        Number("连续对话窗口（分钟）", model.Memory.ConversationMinutes, 1, 1440, v => model.Memory.ConversationMinutes = (int)v);
        Multiline("个人记忆与偏好", model.Memory.Personal, v => model.Memory.Personal = v);
        Add(Ui.Text("此处由你填写的记忆会随问题发送到当前模型服务；不自动提取历史中的内容。不要填写密码或 API Key。历史最多保留 300 段，每段 100 轮；旧版记录会作为一段历史保留。", 12, Muted));
        Label("指令匹配规则");
        Add(Ui.Text("完整匹配触发词，忽略大小写、空格与标点。多个说法用 | 分隔。自定义任务仍使用现有模型和电脑操作权限。", 12, Muted));
        foreach (var rule in model.Memory.Commands.ToArray())
        {
            string[] names = ["DeepSeek 账户查询", "MiniMax 账户查询", "自定义任务"];
            Check("启用规则 · " + names[Math.Clamp((int)rule.Action, 0, 2)], rule.Enabled, v => rule.Enabled = v);
            Field("触发说法（用 | 分隔）", rule.Phrases, v => rule.Phrases = v);
            var action = new ComboBox { ItemsSource = new[] { "查询 DeepSeek 余额", "查询 MiniMax 用量", "交给助手的自定义任务" }.Select(L10n.T).ToArray(), SelectedIndex = (int)rule.Action, Width = 248 };
            SettingRow("匹配后执行", action); save.Add(() => rule.Action = (RememberedAction)action.SelectedIndex);
            int labelIndex = page.Children.Count;
            var task = Box("交给助手的任务", rule.Task); task.Height = 83; task.AcceptsReturn = true; task.TextWrapping = TextWrapping.Wrap;
            var taskLabel = page.Children[labelIndex];
            save.Add(() => rule.Task = task.Text.Trim());
            void ShowTask() { task.Visibility = taskLabel.Visibility = action.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
            action.SelectionChanged += (_, _) => ShowTask(); ShowTask();
            Add(ActionButton("删除这条规则", () => Update(() => model.Memory.Commands.Remove(rule))));
        }
        Add(ActionButton("添加指令记忆", () => Update(() => model.Memory.Commands.Add(new() { Enabled = false, Action = RememberedAction.AssistantTask }))));
        Card("MiniMax 账户类型", "普通语音按量账户的现金余额与资源包目前需在官方控制台查询。国际站 Token Plan 有单独的套餐额度接口，订阅 Key 与语音 Key 不通用。",
            ActionButton("打开 MiniMax 官方控制台", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://platform.minimax.cn/") { UseShellExecute = true })));
        Check("使用 MiniMax 国际站 Token Plan 查询", model.Memory.MiniMaxSubscription, v => model.Memory.MiniMaxSubscription = v);
        Key(ProviderUsage.MiniMaxPlanCredential, "MiniMax 国际站订阅 Key · Windows 凭据管理器");
    }

    private void DispatchPage()
    {
        Card("选择工具与工作空间", "DeepSeek Harness 和 ChatGPT / Codex 使用本机工作目录；WorkBuddy Cloud 使用远端沙箱，不会自动上传本机文件。",
            ActionButton("打开任务派发",()=>{interaction.ShowTasks();Hide();},true));
        string harness=DispatchTools.Harness(model.Dispatch), codex=DispatchTools.Codex(model.Dispatch);
        Card("DeepSeek Harness",harness.Length>0?harness:L10n.T("未检测到"),Ui.Text("沿用已安装运行时，不修改桌面共享配置。",12,Muted));
        Field("Harness 命令路径（留空自动检测）",model.Dispatch.HarnessCommand,v=>model.Dispatch.HarnessCommand=v);
        Check("Harness 复用已配置的官方 DeepSeek 凭据",model.Dispatch.UseDeepSeekCredential,v=>model.Dispatch.UseDeepSeekCredential=v);
        Add(Ui.Text("凭据只传给本次启动进程，不写入任务内容、日志或 Harness 配置。",12,Muted));
        Card("ChatGPT / Codex",codex.Length>0?codex:L10n.T("未检测到"),Ui.Text("使用 Codex 官方本地执行入口和已有登录；不冒充普通网页聊天或云端工作区。",12,Muted));
        Field("Codex 命令路径（留空自动检测）",model.Dispatch.CodexCommand,v=>model.Dispatch.CodexCommand=v);
        Card("WorkBuddy Cloud", "此电脑未安装 WorkBuddy 也能使用其官方云端任务 API。需要开放平台授权 Access Token，模型 API Key 不通用。",
            ActionButton("打开 WorkBuddy 授权说明",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://open.workbuddy.cn/docs/openapi"){UseShellExecute=true})));
        Key(ExternalTaskRunner.WorkBuddyCredential,"WorkBuddy Access Token · Windows 凭据管理器");
        Add(Ui.Text("云端接收不等于完成；网络中断不自动重发。关闭作业窗口后本机任务继续，正常退出整个应用会停止本机任务。",12,Muted));
        foreach(var workspace in model.Dispatch.Workspaces.ToArray())
            Card(workspace.Name,workspace.Directory,ActionButton("移除此工作空间",()=>Update(()=>model.Dispatch.Workspaces.Remove(workspace))));
    }

    private void XiaoAIPage()
    {
        Card("小爱官方技能 · 无需刷机", "用“小爱同学，用电脑助手……”进入技能；电脑执行后，把结果交回小爱播报。",
            Ui.Text("需要先在小爱开放平台配置技能和 HTTPS 服务。普通原生对话不会被本功能监听或接管。",13,Muted));
        Check("连接已配对的小爱官方技能",model.XiaoAI.OfficialEnabled,v => model.XiaoAI.OfficialEnabled = v);
        Field("技能服务 HTTPS 地址",model.XiaoAI.RelayUrl,v => model.XiaoAI.RelayUrl = v);
        Key(XiaoAIRelayClient.CredentialId,"电脑配对密钥 · 仅存 Windows 凭据管理器");
        Add(ActionButton("刷新连接状态",() => status.Text = interaction.OfficialXiaoAI.Status));
        Add(Ui.Text(interaction.OfficialXiaoAI.Status,13,Accent));
        Add(Ui.Text("电脑主动连接技能服务，不在电脑开放控制端口。执行遵循当前电脑自动操作设置，过程仍在电脑显示。耗时任务可用“电脑助手查看结果”查询；不承诺未经真机验证的后台主动播报。",12,Muted));
    }

    private void QqPage()
    {
        Card("QQ 检测", "文件变化和系统通知分别检测，内容只作为数据，不作为自动执行指令。",
            Ui.Text("不读取加密聊天数据库，不注入 QQ 进程。系统通知不等于完整聊天记录。",13,Muted));
        Check("检测 QQ 下载目录的文件变化",model.QQ.FilesEnabled,v => model.QQ.FilesEnabled = v);
        var folder = Box("QQ 下载目录",model.QQ.DownloadDirectory);
        save.Add(() => model.QQ.DownloadDirectory = folder.Text.Trim());
        Add(ActionButton("选择 QQ 下载目录",() =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title="选择 QQ 下载目录" };
            if (dialog.ShowDialog(this) == true) folder.Text = dialog.FolderName;
        }));
        Check("检测 QQ 的 Windows 系统通知",model.QQ.NotificationsEnabled,v => model.QQ.NotificationsEnabled = v);
        Add(ActionButton("请求 Windows 通知访问",() => Run(Request())));
        Check("允许已配对的小爱读取 QQ 检测结果",model.QQ.AllowSpeakerRead,v => model.QQ.AllowSpeakerRead = v);
        Add(ActionButton("查看本机检测结果",() => Run(Preview())));
        Add(Ui.Text("只记录启用期间捕获的变化，最多保留 100 条在内存。QQ 未发送到 Windows 通知中心的消息不会被捕获。文件检测不能判断发送人或保证下载完整。",12,Muted));
        async Task Request() { status.Text = await interaction.QQ.RequestNotificationAccessAsync(); }
        async Task Preview()
        {
            if (model.QQ.NotificationsEnabled) await interaction.QQ.ReadNotificationsAsync();
            var events = interaction.QQ.Snapshot();
            var content = new TextBox { Text=events.Count == 0 ? interaction.QQ.FileStatus + "\n" + interaction.QQ.NotificationStatus :
                string.Join("\n\n",events.Select(e => $"{e.ReceivedAt:g} · {e.Kind}\n{e.Text}")),
                IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
            new Window { Title="QQ · 本机检测结果",Content=content,Width=650,Height=450,Owner=this }.Show();
        }
    }

    private void CopilotPage()
    {
        shortcutDraft = model.Shortcut;
        var record = ActionButton("录制快捷键");
        var selection = Ui.Text("", 13, Ink, true);
        var restore = ActionButton("恢复 Copilot 键", () =>
        {
            shortcutDraft = null;
            FinishRecording();
        });
        var controls = new StackPanel();
        selection.Margin = new Thickness(0, 0, 0, 10);
        controls.Children.Add(selection);
        controls.Children.Add(Ui.Row(record, restore));
        FinishRecording();
        record.Click += (_, _) =>
        {
            IsRecordingShortcut = true;
            record.Content = L10n.T("请按下快捷键");
            selection.Text = L10n.T("录制中。按 Esc 取消。");
            record.Focus();
        };
        record.LostKeyboardFocus += (_, _) => FinishRecording();
        record.PreviewKeyDown += (_, e) =>
        {
            if (!IsRecordingShortcut) return;
            InputKey key = e.Key == InputKey.System ? e.SystemKey : e.Key;
            if (key == InputKey.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
            e.Handled = true;
            if (key == InputKey.Escape && Keyboard.Modifiers == ModifierKeys.None) { FinishRecording(); return; }
            if (e.IsRepeat) return;
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey is 0 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return;
            var candidate = new AssistantShortcut(Keyboard.Modifiers, virtualKey);
            if (!candidate.IsValid)
            {
                selection.Text = L10n.T("请按功能键或组合键，例如 Ctrl + Alt + Space。");
                return;
            }
            shortcutDraft = candidate;
            FinishRecording();
        };
        Card("助手唤醒", "默认使用 Copilot 键。保存后，短按开启语音，双击进入静默输入，长按截图。", controls);

        var details = Ui.Text("", 13, Muted);
        Card("Windows Copilot 键", "使用默认按键时，在 Windows 的 Copilot 键设置中选择 Windows Intelligence。",
            Ui.Row(ActionButton("打开 Windows 按键选择器", Copilot.OpenSettings, true), ActionButton("诊断注册", () => Run(Update()))));
        Add(details);
        Run(Update());
        async Task Update() => details.Text = await Copilot.DiagnoseAsync(fastPath);

        void FinishRecording()
        {
            IsRecordingShortcut = false;
            record.Content = L10n.T("录制快捷键");
            string configured = shortcutDraft?.ToString() ?? "Copilot 键";
            string active = shortcut?.Active?.ToString() ?? "Copilot 键";
            selection.Text = configured == active ? L10n.F("当前使用 {0}","Currently using {0}",L10n.T(active)) : L10n.F("待保存 {0} · 当前使用 {1}","Pending {0} · Currently using {1}",L10n.T(configured),L10n.T(active));
        }
    }

    private void AiPage()
    {
        var provider = model.Provider;
        Providers(model.Providers, p => p.Name, provider, p => model.ActiveProvider = p.Id, () => { var p = new ProviderSettings { Name = "自定义服务", BaseUrl = "", TextModel = "", VisionModel = "", SupportsVision = false }; model.Providers.Add(p); model.ActiveProvider = p.Id; });
        Field("服务名称", provider.Name, v => provider.Name = v);
        Field("API 基础地址", provider.BaseUrl, v => provider.BaseUrl = v);
        Key(provider.Id);
        Field("文本模型", provider.TextModel, v => provider.TextModel = v);
        Field("视觉模型", provider.VisionModel, v => provider.VisionModel = v);
        Check("启用文本输入", provider.SupportsText, v => provider.SupportsText = v);
        Check("所选模型支持流式输出", provider.SupportsStreaming, v => provider.SupportsStreaming = v);
        Check("所选模型支持工具调用", provider.SupportsTools, v => provider.SupportsTools = v);
        Check("请求流式回复", provider.Streaming, v => provider.Streaming = v);
        Number("温度", provider.Temperature, 0, 2, v => provider.Temperature = v);
        Number("回复 Token 上限", provider.MaxTokens, 1, 32768, v => provider.MaxTokens = (int)v);
        Number("请求超时 (秒)", provider.TimeoutSeconds, 5, 300, v => provider.TimeoutSeconds = (int)v);
        Multiline("系统提示词", provider.SystemPrompt, v => provider.SystemPrompt = v);
        Add(ActionButton("保存并测试连接", () => Run(Test())));
        async Task Test()
        {
            if (!Save()) return;
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { status.Text = await new AiClient().TestAsync(provider, SecretStore.Get(provider.Id), ct.Token); }
            catch (OperationCanceledException) { status.Text = "连接测试超时。"; }
        }
    }

    private void TtsPage()
    {
        var provider = model.TtsProvider;
        Choose("朗读模式", ["关闭朗读", "本地 Kokoro", "自定义 API", "ElevenLabs", "MiniMax（HD → Turbo）"], (int)model.Tts, v => model.Tts = (TtsMode)v);
        Check("自动朗读回复", model.AutoSpeak, v => model.AutoSpeak = v);
        Check("助手打开时播放提示音", model.Cue, v => model.Cue = v);
        Number("语速", model.TtsSpeed, .5, 2, v => model.TtsSpeed = v);
        Field("MiniMax 音色", model.MiniMaxVoice, v => model.MiniMaxVoice = v);
        Key(MiniMaxSpeech.CredentialId, "MiniMax API Key · Windows 凭据管理器");
        Add(Ui.Text("MiniMax 默认使用 Speech-2.8-HD，失败后自动使用 Speech-2.8-Turbo。朗读文本会发送到 MiniMax。", 12, Muted));
        Field("ElevenLabs Voice ID", model.ElevenLabsVoice, v => model.ElevenLabsVoice = v);
        Field("ElevenLabs 模型", model.ElevenLabsModel, v => model.ElevenLabsModel = v);
        Key(ElevenLabsSpeech.CredentialId, "ElevenLabs API Key · Windows 凭据管理器");
        Add(Ui.Text("选择 ElevenLabs 后，待朗读的文本会发送到 api.elevenlabs.io。", 12, Muted));
        Number("Kokoro 声音编号（41–52 为中文声音）", model.LocalVoice, 0, 53, v => model.LocalVoice = (int)v);
        Providers(model.TtsProviders, p => p.Name, provider, p => model.ActiveTtsProvider = p.Id, () => { var p = new TtsProviderSettings { Name = "自定义朗读" }; model.TtsProviders.Add(p); model.ActiveTtsProvider = p.Id; });
        Field("服务名称", provider.Name, v => provider.Name = v);
        Field("自定义语音 API 完整地址", provider.Url, v => provider.Url = v);
        Key(provider.Id);
        Field("模型名称", provider.Model, v => provider.Model = v);
        Field("声音 ID", provider.Voice, v => provider.Voice = v);
        Choose("音频格式", ["wav", "mp3"], provider.Format == "mp3" ? 1 : 0, v => provider.Format = v == 1 ? "mp3" : "wav");
        Number("采样率 (0 代表服务默认值)", provider.SampleRate, 0, 192000, v => provider.SampleRate = (int)v);
        Add(ActionButton("保存并试听", () => { if (Save()) Run(interaction.PreviewTtsAsync()); }));
    }

    private void PrivacyPage()
    {
        Check("截图授权后自动发送", model.AutoSend, v => model.AutoSend = v);
        Check("在内存中保留最近一张截图用于下一轮", model.ImageHistory, v => { model.ImageHistory = v; if (!v) interaction.ClearImages(); });
        Check("在本机保存文本对话历史", model.SaveTextHistory, v => { if (!v && model.SaveTextHistory) interaction.ClearHistory(); model.SaveTextHistory = v; });
        string endpoint;
        try { endpoint = AiEndpoint.Create(model.Provider.BaseUrl).Authority; }
        catch (UserError) { endpoint = "当前服务地址无效"; }
        Card("截图发送授权", L10n.F("截图可能包含私人信息。手动发送时可先预览与遮挡。启用自动发送后，主动截图会发送到 {0}。", "Screenshots may contain private information. Preview and redact before sending. Authorized automatic captures are sent to {0}.",endpoint),
            Ui.Row(ActionButton("同意向此服务自动发送", () => Update(() => model.ScreenshotConsent = model.DestinationKey), true),
                ActionButton("撤销授权", () => Update(() => { model.ScreenshotConsent = null; interaction.ClearImages(); }))));
        Add(Ui.Row(ActionButton("清除图片上下文", () => { interaction.ClearImages(); status.Text = "已清除图片上下文。"; }),
            ActionButton("清除对话历史", () => { interaction.ClearHistory(); status.Text = "已清除历史。"; }), ActionButton("查看文本历史", ShowHistory)));
    }

    private async Task SetStartup(bool enable)
    {
        if (Copilot.GetAumid() is null) throw new UserError("开机启动需要已安装的 MSIX 包身份。");
        var task = await StartupTask.GetAsync("WindowsIntelligenceStartup");
        if (enable) status.Text = $"Windows 开机启动状态：{await task.RequestEnableAsync()}";
        else { task.Disable(); status.Text = "已关闭开机启动。"; }
    }

    private void ShowHistory()
    {
        interaction.ShowConversations();
    }

    private void Providers<T>(IReadOnlyList<T> items, Func<T, string> name, T active, Action<T> select, Action add)
    {
        var menu = new ComboBox { ItemsSource = items.Select(name).ToArray(), SelectedIndex = items.ToList().IndexOf(active), Width = 225, Height = 31, Margin = new Thickness(0, 0, 9, 0) };
        menu.SelectionChanged += (_, _) => { if (menu.SelectedIndex >= 0) Update(() => select(items[menu.SelectedIndex])); };
        Add(Ui.Row(menu, ActionButton("添加服务", () => Update(add))));
    }

    private void Update(Action update)
    {
        if (!Save()) return;
        try { update(); model.Save(); Render(); }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void Card(string title, string detail, UIElement tail)
    {
        var box = new StackPanel();
        box.Children.Add(Ui.Text(title, 16, Ink, true));
        var label = Ui.Text(detail, 13, Muted); label.Margin = new Thickness(0, 5, 0, 13);
        box.Children.Add(label); box.Children.Add(tail);
        if (tail is Button button) button.HorizontalAlignment = HorizontalAlignment.Left;
        page.Children.Add(new Border { Child = box, Padding = new Thickness(21), Background = SystemParameters.HighContrast ? Surface : GlassFill("#EEFFFFFF", "#BFFFFFFF"),
            BorderBrush = SystemParameters.HighContrast ? Line : GlassFill("#FFFFFFFF", "#2794A1BD"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 0, 15) });
    }

    private void Add(FrameworkElement item)
    {
        item.Margin = new Thickness(0, 0, 0, 11);
        item.HorizontalAlignment = item is CheckBox or TextBlock ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        page.Children.Add(item);
    }

    private void Label(string title) { var label = Ui.Text(title, 13, Muted, true); label.Margin = new Thickness(0, 1, 0, 6); page.Children.Add(label); }

    private void SettingRow(string title, Control input)
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(input, Dock.Right);
        row.Children.Add(input);
        var label = Ui.Text(title, 14, Ink);
        label.Margin = new Thickness(0, 3, 12, 3);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        page.Children.Add(new Border { Child = row, Padding = new Thickness(16, 10, 12, 10), Background = Surface,
            BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 0, 9) });
    }

    private TextBox Box(string title, string value)
    {
        Label(title);
        var box = new TextBox { Text = value, MaxWidth = 620, MinHeight = 34, Foreground = Ink,
            Background = Canvas, BorderBrush = Line, Padding = new Thickness(9, 5, 9, 5), CaretBrush = Accent };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        box.Margin = new Thickness(0, 0, 0, 12);
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        page.Children.Add(box);
        return box;
    }

    private void Field(string title, string value, Action<string> set) { var box = Box(title, value); save.Add(() => set(box.Text.Trim())); }
    private void Multiline(string title, string value, Action<string> set)
    {
        var box = Box(title, value); box.Height = 83; box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; save.Add(() => set(box.Text.Trim()));
    }

    private void Number(string title, double value, double min, double max, Action<double> set)
    {
        var box = new TextBox { Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 96, MinHeight = 33,
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right,
            Background = Canvas, Foreground = Ink, BorderBrush = Line, CaretBrush = Accent, Padding = new Thickness(8, 3, 8, 3) };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        SettingRow(title, box);
        save.Add(() =>
        {
            if (!double.TryParse(box.Text, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
                throw new UserError($"{title} 的范围是 {min}–{max}。");
            set(n);
        });
    }

    private void Check(string title, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = Ui.Text(title, 14, Ink), IsChecked = value };
        check.Loaded += (_, _) => PositionThumb(false);
        check.Checked += (_, _) => PositionThumb(true);
        check.Unchecked += (_, _) => PositionThumb(true);
        System.Windows.Automation.AutomationProperties.SetName(check, title);
        Add(check); save.Add(() => set(check.IsChecked == true));
        void PositionThumb(bool animate)
        {
            if (check.Template?.FindName("Thumb", check) is not System.Windows.Shapes.Ellipse thumb) return;
            if (thumb.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
                thumb.RenderTransform = transform = new TranslateTransform();
            double from = transform.X, to = check.IsChecked == true ? 17 : 0;
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = to;
            if (animate && GlassMotion.Enabled(model.ReduceMotion))
                transform.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        }
    }

    private void Choose(string title, string[] options, int index, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = options.Select(L10n.T).ToArray(), SelectedIndex = Math.Clamp(index, 0, options.Length - 1), Width = 248 };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        SettingRow(title, box); save.Add(() => set(box.SelectedIndex));
    }

    private void Key(Guid id, string label = "API Key · Windows 凭据管理器")
    {
        Label(label);
        var box = new PasswordBox { Password = SecretStore.Get(id), MaxWidth = 620, Height = 34, Foreground = Ink,
            Background = Canvas, BorderBrush = Line, Padding = new Thickness(9, 5, 9, 5), CaretBrush = Accent };
        System.Windows.Automation.AutomationProperties.SetName(box, "API Key");
        box.Margin = new Thickness(0, 0, 0, 12);
        page.Children.Add(box);
        save.Add(() => { if (box.Password != SecretStore.Get(id)) SecretStore.Set(id, box.Password); });
    }

    private bool Save()
    {
        try
        {
            if (tab == 2 && shortcut is not null)
            {
                AssistantShortcut? previous = model.Shortcut;
                shortcut.Apply(shortcutDraft, () =>
                {
                    model.Shortcut = shortcutDraft;
                    try { model.Save(); }
                    catch { model.Shortcut = previous; throw; }
                });
                Render();
            }
            else
            {
                foreach (var update in save) update();
                CommandMemory.Validate(model.Memory);
                if (model.XiaoAI.OfficialEnabled)
                {
                    _ = XiaoAIRelayClient.BaseAddress(model.XiaoAI.RelayUrl);
                    if (SecretStore.Get(XiaoAIRelayClient.CredentialId).Length < 32) throw new UserError("请保存至少 32 字符的电脑配对密钥。");
                }
                if (model.QQ.FilesEnabled && (!Directory.Exists(model.QQ.DownloadDirectory) ||
                    (File.GetAttributes(model.QQ.DownloadDirectory) & FileAttributes.ReparsePoint) != 0)) throw new UserError("QQ 下载目录不存在或是目录链接。");
                model.Save();
                interaction.ApplyIntegrations();
            }
            if (L10n.Language != model.Language)
            {
                L10n.Language = model.Language;
                Render(); L10n.Apply(this); interaction.ApplyLanguage();
            }
            status.Text = L10n.T("设置已保存。");
            return true;
        }
        catch (Exception ex) { status.Text = ex.Message; return false; }
    }

    private void Run(Task work) => Ui.Observe(work, ex => status.Text = ex is HttpRequestException ? "无法连接服务。" : ex.Message);
}
