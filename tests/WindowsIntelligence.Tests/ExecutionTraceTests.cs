using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsIntelligence;
using Screen = System.Windows.Forms.Screen;

internal static class ExecutionTraceTests
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync()
    {
        const string command = "Write-Output '第一段实时输出'; Start-Sleep -Milliseconds 1800; Write-Output '执行结束'";
        var journal = new ExecutionJournal("sk-fixture-never-a-real-key");
        int id = journal.Begin(command, "读取电脑状态并等待返回");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = PowerShellRunner.RunAsync(command, CancellationToken.None, TimeSpan.FromSeconds(12), (error, text) =>
        {
            journal.Append(id, error, text);
            if (text.Contains("第一段")) first.TrySetResult();
        });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(!run.IsCompleted && journal.Snapshot()[0].Output.Contains("第一段"),
            "Output was hidden until the command completed");
        var result = await run;
        journal.Complete(id, result);
        Require(journal.Snapshot()[0] is { State: "已完成", ExitCode: 0 } &&
            journal.Snapshot()[0].Output.Contains("执行结束"), "Real command result was lost");

        int errorId = journal.Begin("throw '演示错误'", "检查错误状态");
        var errorResult = await PowerShellRunner.RunAsync("throw '演示错误'", CancellationToken.None);
        journal.Complete(errorId, errorResult);
        Require(journal.Snapshot()[1] is { State: "执行失败", ExitCode: 1 } &&
            journal.Snapshot()[1].Error.Contains("演示错误"), "Failed command did not remain visible");

        var privateJournal = new ExecutionJournal("sk-fixture-never-a-real-key");
        int privateId = privateJournal.Begin("Write-Output 'sk-fixture-never-a-real-key'", "测试脱敏");
        privateJournal.Append(privateId, false, "sk-fixture-never");
        Require(!privateJournal.Snapshot()[0].Output.Contains("sk-fixture"), "Split credential leaked into live preview");
        privateJournal.Append(privateId, false, "-a-real-key");
        privateJournal.Complete(privateId, new(0, "sk-fixture-never-a-real-key\nAuthorization: Bearer fixture-token", ""));
        var redacted = privateJournal.Snapshot()[0];
        Require(!redacted.Command.Contains("sk-fixture") && !redacted.Output.Contains("fixture-token"),
            "Command or output credentials were not redacted");
        int cancelId = privateJournal.Begin("Start-Sleep -Seconds 20", "测试取消");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(450)))
        {
            try { await PowerShellRunner.RunAsync("Start-Sleep -Seconds 20", cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { privateJournal.Complete(cancelId, null, "已取消"); }
        }
        Require(privateJournal.Snapshot()[1].State == "已取消", "Cancellation lost the execution record");

        using var caption = new Caption { ReduceMotion = true, Silent = true };
        caption.State("回复完成", "检查一下电脑", "已完成检查，执行记录保留在上方。", false, false, "", steps: journal.Snapshot());
        caption.Show();
        caption.Place(Screen.PrimaryScreen!, 1);
        await Task.Delay(180);
        var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Windows Intelligence · 助手");
        var commandBox = FindText(window, "第 1 步 PowerShell 命令");
        var outputBox = FindText(window, "第 1 步返回结果");
        var errorBox = FindText(window, "第 2 步错误输出");
        Require(commandBox is { IsVisible: true, IsReadOnly: true } && commandBox.Text == command &&
            outputBox?.Text.Contains("执行结束") == true && errorBox?.Text.Contains("演示错误") == true,
            "The user cannot inspect commands, results and errors");
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "execution-trace-preview.png"))) encoder.Save(file);
        caption.State("输入问题", "", "", true, false, "");
        Require(FindText(window, "第 1 步 PowerShell 命令") is null, "A new session retained the previous trace");
    }

    private static TextBox? FindText(DependencyObject parent, string name)
    {
        if (parent is TextBox text && AutomationProperties.GetName(text) == name) return text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindText(VisualTreeHelper.GetChild(parent, index), name) is { } found) return found;
        return null;
    }

    internal static async Task FlowAsync()
    {
        var settings = new Settings { ReduceMotion = true, Cue = false, AutoSpeak = false,
            SaveTextHistory = false, PowerShellEnabled = true };
        settings.Provider.BaseUrl = "http://127.0.0.1:11434/v1";
        settings.Provider.SupportsTools = true;
        using var http = new HttpClient(new ToolFlow());
        using var overlay = new Overlay(settings);
        using var caption = new Caption();
        using var interaction = new Interaction(settings, Dispatcher.CurrentDispatcher, overlay, caption, new AiClient(http),
            (_, _) => Task.FromResult(""));
        try
        {
            interaction.Start(false, typed: true, silent: true);
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Windows Intelligence · 助手");
            FindText(window, "输入问题")!.Text = "请实际检查测试输出。";
            Button? Send(DependencyObject parent)
            {
                if (parent is Button button && AutomationProperties.GetName(button) == "发送问题") return button;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    if (Send(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
                return null;
            }
            Send(window)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => FindText(window, "第 1 步返回结果")?.Text.Contains("实时流") == true);
            Require(!interaction.HistoryText.Contains("检查完毕"), "The output was only rendered after the final answer");
            await Until(() => interaction.HistoryText.Contains("检查完毕"));
            Require(FindText(window, "第 1 步 PowerShell 命令")?.Text.Contains("Write-Output") == true &&
                FindText(window, "第 1 步返回结果")?.Text.Contains("最终结果") == true,
                "Interaction dropped the command or result when it rendered the answer");
        }
        finally { interaction.Dispose(); await interaction.ResourceDisposal; }

        static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(80); }
            throw new InvalidOperationException("The execution UI did not reach the expected state");
        }
    }

    private sealed class ToolFlow : HttpMessageHandler
    {
        private int step;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object message = step++ == 0 ? new
            {
                role = "assistant", content = (string?)null,
                tool_calls = new[] { new { id = "visible-tool", type = "function", function = new
                {
                    name = "run_powershell", arguments = JsonSerializer.Serialize(new
                    {
                        command = "Write-Output '实时流'; Start-Sleep -Milliseconds 1800; Write-Output '最终结果'",
                        description = "检查实时输出通道"
                    })
                } } }
            } : new { role = "assistant", content = "检查完毕。" };
            string payload = JsonSerializer.Serialize(new { choices = new[] { new { message,
                finish_reason = step == 1 ? "tool_calls" : "stop" } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
