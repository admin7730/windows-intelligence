using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace WindowsIntelligence;

internal sealed record DispatchSelection(DispatchWorkspace Project, NativeDispatchThread? Thread, bool ReadOnly);

internal sealed class DispatchPicker
{
    private sealed record ProjectOption(string Name, DispatchWorkspace? Project)
    {
        public override string ToString() => Name;
    }

    private sealed record ThreadOption(string Name, NativeDispatchThread? Thread)
    {
        public override string ToString() => Name;
    }

    private readonly Window window = OverlayUi.Window("选择派发位置", focus: true);
    private readonly ComboBox projects = new() { MinHeight = 34 };
    private readonly TextBox search = new() { MinHeight = 34, Padding = new Thickness(8, 4, 8, 4), BorderBrush = Ui.Brush("#BEC9EB") };
    private readonly ListBox threads = new() { Height = 186, Background = Ui.Brush("#EEF2FB"), BorderBrush = Ui.Brush("#D1DAEF") };
    private readonly CheckBox readOnly = new() { Content = L10n.T("只读任务"), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = Ui.Text("", 12, Ui.Brush("#586480"));
    private readonly TextBox newName = new() { MinHeight = 34, Padding = new Thickness(8, 4, 8, 4) };
    private readonly StackPanel newProject = new() { Visibility = Visibility.Collapsed };
    private readonly TaskCompletionSource<DispatchSelection?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Settings settings;
    private readonly DispatchIntent intent;
    private readonly Button send;
    private readonly Button reload;
    private readonly CancellationTokenSource closed = new();
    private IReadOnlyList<NativeDispatchThread> catalog = [];
    private bool loading;

    internal DispatchPicker(Settings settings, DispatchIntent intent, Window owner, Screen screen, double scale)
    {
        this.settings = settings;
        this.intent = intent;
        window.Owner = owner;
        string target = intent.Target == DispatchTarget.DeepSeekHarness ? "DeepSeek Harness" : "ChatGPT / Codex";
        var content = new StackPanel { Width = Math.Min(488, screen.WorkingArea.Width / scale - 54) };
        content.Children.Add(Ui.Text("选择派发位置", 23, Ui.Brush("#1D274A"), true));
        content.Children.Add(Ui.Text(target, 13, Ui.Brush("#5261A1"), true));
        var task = Ui.Text("", 14, Ui.Brush("#344063"));
        task.Text = TaskDispatch.Bound(TaskDispatch.Redact(intent.Task), 350);
        task.Margin = new Thickness(0, 8, 0, 12);
        task.MaxHeight = 56;
        L10n.Preserve(task);
        content.Children.Add(task);

        AddLabel(content, "项目");
        var projectRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var newButton = Button("新建项目", BeginProject);
        DockPanel.SetDock(newButton, Dock.Right);
        projectRow.Children.Add(newButton);
        projectRow.Children.Add(projects);
        content.Children.Add(projectRow);
        AddLabel(newProject, "项目名称");
        newProject.Children.Add(newName);
        newProject.Children.Add(Ui.Text("发送时创建项目目录，并在其中开启新对话。", 12, Ui.Brush("#586480")));
        content.Children.Add(newProject);
        AddLabel(content, "搜索对话或目录");
        content.Children.Add(search);
        threads.Margin = new Thickness(0, 7, 0, 8);
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 6, 9, 6)));
        threads.ItemContainerStyle = rowStyle;
        content.Children.Add(threads);
        content.Children.Add(status);
        var actions = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        send = Button("发送任务", Confirm, primary: true);
        var cancel = Button("取消", window.Close);
        reload = Button("刷新", () => _ = LoadAsync());
        foreach (var button in new[] { send, cancel, reload })
        {
            DockPanel.SetDock(button, Dock.Right);
            actions.Children.Add(button);
        }
        readOnly.IsChecked = !settings.PowerShellEnabled;
        readOnly.IsEnabled = settings.PowerShellEnabled;
        actions.Children.Add(readOnly);
        content.Children.Add(actions);

        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(180, screen.WorkingArea.Height / scale - 80) };
        var surface = new Border
        {
            Child = scroll, Padding = new Thickness(16), CornerRadius = new CornerRadius(20),
            Background = Ui.Brush("#FAF8F9FF"), BorderBrush = Ui.Brush("#BEC9EB"), BorderThickness = new Thickness(1)
        };
        window.Content = surface;
        window.Loaded += (_, _) =>
        {
            int right = screen.WorkingArea.Right - (int)Math.Round((content.Width + 49) * scale);
            int top = screen.WorkingArea.Top + (int)Math.Round(23 * scale);
            OverlayUi.Position(window, Math.Max(screen.WorkingArea.Left, right), top);
            window.Activate();
            search.Focus();
        };
        window.Closed += (_, _) => { closed.Cancel(); result.TrySetResult(null); };
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
        projects.SelectionChanged += (_, _) => { newProject.Visibility = Visibility.Collapsed; RefreshThreads(); };
        search.TextChanged += (_, _) => RefreshThreads();
        newName.TextChanged += (_, _) => UpdateSend();
        threads.SelectionChanged += (_, _) => UpdateSend();
        System.Windows.Automation.AutomationProperties.SetName(search, L10n.T("搜索对话或目录"));
        System.Windows.Automation.AutomationProperties.SetName(newName, L10n.T("项目名称"));
        System.Windows.Automation.AutomationProperties.SetName(projects, L10n.T("项目"));
        System.Windows.Automation.AutomationProperties.SetName(threads, L10n.T("已有对话"));
    }

    internal async Task<DispatchSelection?> ShowAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var registration = cancellation.Register(() => window.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible) window.Close();
        }));
        window.Show();
        _ = LoadAsync();
        return await result.Task;
    }

    private async Task LoadAsync()
    {
        if (loading || closed.IsCancellationRequested) return;
        loading = true;
        reload.IsEnabled = false;
        UpdateSend();
        status.Text = L10n.T("正在读取已有项目和对话…");
        try
        {
            DispatchCatalog native;
            string error = "";
            try { native = await DispatchCatalog.LoadAsync(intent.Target, settings, closed.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = L10n.T(ex is UserError ? ex.Message : "暂时无法读取本机对话索引。");
                native = new([], []);
            }
            if (closed.IsCancellationRequested) return;
            catalog = native.Threads.DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(t => t.Updated).ToArray();
            var items = native.Projects.Concat(settings.Dispatch.Workspaces)
                .Concat(catalog.Select(t => new DispatchWorkspace { Name = DispatchCatalog.ProjectName(t.Directory), Directory = t.Directory }))
                .Where(p => Directory.Exists(p.Directory))
                .DistinctBy(p => p.Directory, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new ProjectOption(p.ToString(), p)).ToArray();
            var previous = (projects.SelectedItem as ProjectOption)?.Project?.Directory;
            projects.ItemsSource = new[] { new ProjectOption(L10n.T("所有项目"), null) }.Concat(items).ToArray();
            projects.SelectedItem = items.FirstOrDefault(p => p.Project?.Directory == previous) ?? projects.Items[0];
            status.Text = error.Length > 0 ? error : L10n.F("找到 {0} 个对话。选择已有对话，或在项目中新建对话。", "Found {0} chats. Choose one or start a new chat in a project.", catalog.Count);
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        finally
        {
            loading = false;
            reload.IsEnabled = true;
            UpdateSend();
        }
    }

    private void RefreshThreads()
    {
        var selected = (threads.SelectedItem as ThreadOption)?.Thread?.Id;
        string query = search.Text.Trim();
        var project = (projects.SelectedItem as ProjectOption)?.Project;
        var items = catalog.Where(t => project is null || t.Directory.Equals(project.Directory, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || t.Directory.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(t => new ThreadOption((t.Title.Length == 0 ? L10n.T("未命名对话") : TaskDispatch.Bound(TaskDispatch.Redact(t.Title.ReplaceLineEndings(" ")), 100))
                + "\n" + t.Directory + (!Directory.Exists(t.Directory) ? "  ·  " + L10n.T("目录不可用")
                    : t.Updated == default ? "" : "  ·  " + t.Updated.ToLocalTime().ToString("g")), t)).ToList();
        if (project is not null && Directory.Exists(project.Directory))
            items.Insert(0, new(L10n.T("＋ 新建对话"), null));
        threads.ItemsSource = items;
        if (selected is not null) threads.SelectedItem = items.FirstOrDefault(t => t.Thread?.Id == selected);
        else if (project is not null && items.Count > 0 && items[0].Thread is null) threads.SelectedIndex = 0;
        UpdateSend();
    }

    private void BeginProject()
    {
        newProject.Visibility = Visibility.Visible;
        threads.SelectedItem = null;
        newName.Focus();
        UpdateSend();
    }

    private void UpdateSend()
    {
        send.IsEnabled = !loading && (newProject.Visibility == Visibility.Visible
            ? newName.Text.Trim().Length > 0
            : threads.SelectedItem is ThreadOption option && (option.Thread is { } thread
                ? Directory.Exists(thread.Directory) : (projects.SelectedItem as ProjectOption)?.Project is not null));
    }

    private void Confirm()
    {
        if (!send.IsEnabled) return;
        try
        {
            DispatchWorkspace project;
            NativeDispatchThread? thread = null;
            if (newProject.Visibility == Visibility.Visible) project = CreateProject(newName.Text.Trim());
            else
            {
                thread = (threads.SelectedItem as ThreadOption)?.Thread;
                project = (projects.SelectedItem as ProjectOption)?.Project
                    ?? new() { Name = DispatchCatalog.ProjectName(thread!.Directory), Directory = thread.Directory };
                TaskDispatch.ValidateWorkspace(project.Directory);
            }
            result.TrySetResult(new(project, thread, readOnly.IsChecked == true));
            window.Close();
        }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException or ArgumentException)
        {
            status.Text = L10n.T(ex is UserError ? ex.Message : "无法创建或打开项目，请检查目录权限。");
        }
    }

    private static DispatchWorkspace CreateProject(string name)
    {
        string prefix = name.Split('.')[0].TrimEnd();
        bool reserved = System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (name.Length is 0 or > 64 || name.EndsWith('.') || reserved || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UserError("请输入有效的项目名称，最多 64 个字符。");
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Windows Intelligence Projects");
        string path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new UserError("这个项目名称已存在，请使用其他名称。");
        Directory.CreateDirectory(path);
        return new() { Name = name, Directory = path };
    }

    private static void AddLabel(Panel container, string text)
    {
        var label = Ui.Text(text, 12, Ui.Brush("#536080"), true);
        label.Margin = new Thickness(0, 4, 0, 4);
        container.Children.Add(label);
    }
    private static Button Button(string label, Action action, bool primary = false)
    {
        var button = Ui.Button(label, action, primary);
        button.Background = Ui.Brush(primary ? "#4C63BC" : "#E8ECFC");
        button.Foreground = Ui.Brush(primary ? "#FFFFFF" : "#344881");
        button.BorderBrush = Ui.Brush(primary ? "#4C63BC" : "#C6D0F2");
        return button;
    }
}
