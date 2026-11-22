using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsIntelligence;

internal sealed class ConversationWindow : Window
{
    private readonly ListBox entries = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly MarkdownReplyView transcript = new() { Light = true };
    private readonly TextBlock heading = Ui.Text("选择一段对话", 23, Ui.Brush("#222838"), true);
    private readonly TextBlock info = Ui.Text("", 12, Ui.Brush("#657084"));
    private readonly TextBox search = new() { Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(12), ToolTip = "搜索对话" };
    private readonly Interaction interaction;
    private Conversation? selected;

    internal ConversationWindow(Interaction interaction)
    {
        this.interaction = interaction;
        L10n.Preserve(heading);
        Title = "Windows Intelligence · 历史对话"; Width = 940; Height = 680; MinWidth = 720; MinHeight = 480;
        Background = Ui.Brush("#F2F4F8"); FontFamily = Ui.Body; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WindowsIntelligence;component/SettingsTheme.xaml", UriKind.Relative) });
        foreach (var entry in new[] { ("SettingsCanvas","#F2F4F8"), ("SettingsSurface","#EFFFFFFF"), ("SettingsInk","#222838"),
            ("SettingsMuted","#657084"), ("SettingsLine","#1A263858"), ("SettingsAccent","#0869E8"), ("SettingsSelected","#DCE9FF") }) Resources[entry.Item1] = Ui.Brush(entry.Item2);
        search.Style = (Style)FindResource("SettingsSearch"); search.Tag = "搜索对话";
        var layout = new Grid { Background = Background }; layout.ColumnDefinitions.Add(new() { Width = new GridLength(246) }); layout.ColumnDefinitions.Add(new());
        var sidebar = new DockPanel { Background = Ui.Brush("#E8EDF7") };
        var newButton = Button("新对话", () => { interaction.NewConversation(); Close(); });
        newButton.Margin = new Thickness(12, 15, 12, 0); DockPanel.SetDock(newButton, Dock.Top); sidebar.Children.Add(newButton);
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索历史对话");
        DockPanel.SetDock(search, Dock.Top); sidebar.Children.Add(search); sidebar.Children.Add(entries); layout.Children.Add(sidebar);
        var main = new DockPanel { Margin = new Thickness(25, 22, 22, 20) }; Grid.SetColumn(main, 1); layout.Children.Add(main);
        var header = new StackPanel(); header.Children.Add(heading); info.Margin = new Thickness(0, 7, 0, 16); header.Children.Add(info);
        DockPanel.SetDock(header, Dock.Top); main.Children.Add(header);
        var actions = new WrapPanel { Margin = new Thickness(0, 15, 0, 0) };
        var resume = Button("继续此对话", () => { if (selected is { } item) { interaction.ResumeConversation(item.Id); Close(); } });
        var remove = Button("删除此对话", () =>
        {
            if (selected is null) return;
            if (interaction.Conversations.Active?.Id == selected.Id) interaction.Cancel();
            interaction.Conversations.Delete(selected.Id); selected = null; Refresh();
        });
        actions.Children.Add(resume); actions.Children.Add(remove); DockPanel.SetDock(actions, Dock.Bottom); main.Children.Add(actions);
        main.Children.Add(new ScrollViewer { Content = transcript, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
        search.TextChanged += (_, _) => Refresh();
        entries.SelectionChanged += (_, _) =>
        {
            selected = (entries.SelectedItem as ListBoxItem)?.Tag as Conversation;
            heading.Text = selected?.Title ?? L10n.T("暂无对话");
            info.Text = selected is null ? L10n.T(interaction.Conversations.Warning ?? "开始提问后，对话会显示在这里。") :
                L10n.F("{0:g} · {1} 轮 · 继续后仅使用这段对话的上下文", "{0:g} · {1} turns · Only this conversation is used when you continue", selected.UpdatedAt.ToLocalTime(),selected.Turns.Count);
            transcript.SetMarkdown(selected is null ? "" : string.Join("\n\n---\n\n", selected.Turns.Select(t => (L10n.Language==AppLanguage.English?"**You**":"**你**") + "\n\n" + t.Text + "\n\n**Windows Intelligence**\n\n" + t.Answer)), true);
            resume.IsEnabled = remove.IsEnabled = selected is not null;
        };
        Refresh();
        Loaded += (_, _) => L10n.Apply(this);
    }

    private void Refresh()
    {
        Guid? previous = selected?.Id;
        entries.Items.Clear();
        foreach (var item in interaction.Conversations.Items.Where(c => c.Turns.Count > 0 &&
            (search.Text.Length == 0 || c.Title.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) || c.Turns.Any(t => t.Text.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))))
        {
            var content = new StackPanel(); var name=Ui.Text("",13,Ui.Brush("#222838"),true); name.Text=item.Title; L10n.Preserve(name); content.Children.Add(name);
            content.Children.Add(Ui.Text(item.UpdatedAt.ToLocalTime().ToString("g"), 11, Ui.Brush("#657084")));
            entries.Items.Add(new ListBoxItem { Content = content, Tag = item, Padding = new Thickness(11), Margin = new Thickness(7, 2, 7, 2), Style = (Style)FindResource("SettingsNavItem") });
        }
        entries.SelectedItem = entries.Items.OfType<ListBoxItem>().FirstOrDefault(e => (e.Tag as Conversation)?.Id == previous) ?? entries.Items.OfType<ListBoxItem>().FirstOrDefault();
        if (entries.Items.Count == 0) { heading.Text = "暂无对话"; info.Text = interaction.Conversations.Warning ?? "开始提问后，对话会显示在这里。"; transcript.SetMarkdown("", true); }
    }

    private static Button Button(string title, Action action)
    {
        var button = Ui.Button(title, action); button.Background = Brushes.White; button.Foreground = Ui.Brush("#2455A1"); button.BorderBrush = Ui.Brush("#CAD3E5");
        return button;
    }
}
