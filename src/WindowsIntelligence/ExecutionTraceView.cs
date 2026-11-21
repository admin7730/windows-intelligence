using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsIntelligence;

internal sealed class ExecutionTraceView : Border
{
    private static readonly Brush Ink = Ui.Brush("#283249"), Quiet = Ui.Brush("#66718B"),
        Running = Ui.Brush("#5566BE"), Success = Ui.Brush("#227D6C"), Failure = Ui.Brush("#B2475D");
    private readonly TextBlock title = Ui.Text("执行过程", 12, Quiet, true);
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroll;
    private readonly Dictionary<int, StepRow> items = [];
    private sealed record StepRow(Expander Expander, TextBlock Header, TextBlock Description, TextBox Command,
        TextBlock OutputLabel, TextBox Output, TextBlock ErrorLabel, TextBox Error);

    internal ExecutionTraceView()
    {
        CornerRadius = new CornerRadius(13);
        Background = Ui.Brush("#70E8EDFA");
        BorderBrush = Ui.Brush("#BAC7D4EE");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(11, 9, 11, 9);
        Margin = new Thickness(0, 0, 0, 10);
        Visibility = Visibility.Collapsed;
        scroll = new ScrollViewer { Content = rows, MaxHeight = 218,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var content = new StackPanel();
        title.Margin = new Thickness(0, 0, 0, 6);
        content.Children.Add(title);
        content.Children.Add(scroll);
        Child = content;
        System.Windows.Automation.AutomationProperties.SetName(this, "电脑操作执行过程");
    }

    internal void Update(IReadOnlyList<ExecutionStep>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            rows.Children.Clear(); items.Clear(); Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        bool follow = scroll.ScrollableHeight - scroll.VerticalOffset < 16;
        title.Text = $"执行过程 · {steps.Count} 次操作";
        foreach (var step in steps)
        {
            if (!items.TryGetValue(step.Id, out var row))
            {
                var body = new StackPanel { Margin = new Thickness(5, 7, 4, 8) };
                var description = Ui.Text(step.Description, 12, Ink);
                description.Margin = new Thickness(0, 0, 0, 7);
                var command = TextArea(step.Command, 88);
                System.Windows.Automation.AutomationProperties.SetName(command, $"第 {step.Id} 步 PowerShell 命令");
                TextBlock outputLabel = Label("返回结果"), errorLabel = Label("错误输出");
                TextBox output = TextArea("", 105), error = TextArea("", 105);
                error.Foreground = Failure;
                System.Windows.Automation.AutomationProperties.SetName(output, $"第 {step.Id} 步返回结果");
                System.Windows.Automation.AutomationProperties.SetName(error, $"第 {step.Id} 步错误输出");
                foreach (UIElement child in new UIElement[] { description, command, outputLabel, output, errorLabel, error })
                    body.Children.Add(child);
                var header = Ui.Text("", 12, Running, true);
                var expander = new Expander { Header = header, Content = body, IsExpanded = true,
                    Margin = new Thickness(0, 3, 0, 3), Foreground = Ink };
                row = new(expander, header, description, command, outputLabel, output, errorLabel, error);
                items.Add(step.Id, row); rows.Children.Add(expander);
            }
            string exit = step.ExitCode is { } code ? $" · 退出码 {code}" : "";
            row.Header.Text = $"{step.Id:00}  {step.State} · {step.Seconds:F1} 秒{exit}";
            row.Header.Foreground = step.State is "正在执行" or "等待语音生成" ? Running : step.State == "已完成" ? Success : Failure;
            row.Expander.ToolTip = step.Description;
            SetText(row.Command, step.Command);
            SetText(row.Output, step.ResultsWithheld ? "首段语音就绪后显示结果" : step.Output.Length > 0 ? step.Output
                : step.State == "正在执行" ? "等待返回…" : "（没有标准输出）");
            SetText(row.Error, step.Error);
            row.Error.Visibility = row.ErrorLabel.Visibility = step.Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (follow) scroll.Dispatcher.InvokeAsync(() => scroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void SetText(TextBox box, string value)
    {
        if (box.Text == value) return;
        int start = box.SelectionStart, length = box.SelectionLength;
        box.Text = value;
        box.Select(Math.Min(start, value.Length), Math.Min(length, Math.Max(0, value.Length - start)));
    }

    private static TextBlock Label(string text) => new() { Text = text, FontFamily = Ui.Body, FontSize = 11,
        Foreground = Quiet, Margin = new Thickness(0, 7, 0, 4) };

    private static TextBox TextArea(string text, double height) => new()
    {
        Text = text, IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 11.5, Foreground = Ink,
        Background = Ui.Brush("#80FFFFFF"), BorderThickness = new Thickness(0), Padding = new Thickness(7),
        TextWrapping = TextWrapping.Wrap, MaxHeight = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsReadOnlyCaretVisible = true
    };
}
