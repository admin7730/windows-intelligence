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
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using WindowsIntelligence;

internal static class SpeechTimingTests
{
    internal static async Task RunAsync()
    {
        await CaseAsync(0); // Even a short first clip releases results while the next is pending.
        await CaseAsync(1); // ESC during generation must never reveal a late reply.
        await CaseAsync(2); // Failure must release readable text rather than wait forever.
    }

    private static async Task CaseAsync(int mode)
    {
        var settings = new Settings { AutoSpeak = true, Tts = TtsMode.MiniMax, Cue = false,
            ReduceMotion = true, SaveTextHistory = false, PowerShellEnabled = true };
        settings.Provider.BaseUrl = "http://127.0.0.1:11434/v1";
        var first = new TaskCompletionSource<WaveStream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<WaveStream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPlayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int generated = 0, played = 0;
        Task<WaveStream?> Synthesize(string text, CancellationToken ct)
        {
            if (mode == 2) return Task.FromResult<WaveStream?>(null);
            if (Interlocked.Increment(ref generated) == 1) { entered.TrySetResult(); return first.Task; }
            secondEntered.TrySetResult(); return second.Task;
        }
        async Task Play(SpeechPipeline voice, CancellationToken ct)
        {
            if (mode == 0) Require(!voice.Drained, "Playback waited for the entire reply");
            while (true)
            {
                if (voice.TakeNextClip() is { } clip)
                {
                    played++; clip.Dispose(); firstPlayed.TrySetResult();
                }
                else if (voice.Drained) return;
                else await Task.Delay(10, ct);
            }
        }
        using var http = new HttpClient(new ReplyTransport());
        using var overlay = new Overlay(settings);
        using var caption = new Caption();
        using var interaction = new Interaction(settings, Dispatcher.CurrentDispatcher, overlay, caption, new AiClient(http),
            (_, _) => Task.FromResult(""), Synthesize, Play);
        try
        {
            interaction.Start(false, typed: true);
            var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Windows Intelligence · 助手");
            Find<TextBox>(window, x => AutomationProperties.GetName(x) == "输入问题")!.Text = "检查并朗读结果。";
            Find<Button>(window, x => AutomationProperties.GetName(x) == "发送问题")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (mode == 2)
            {
                await Until(() => VisibleReply(window).Contains("检查完毕"));
                Require(Output(window).Contains("RESULT-42"), "TTS failure withheld the result forever");
                return;
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(180);
            Require(Find<TextBox>(window, x => AutomationProperties.GetName(x) == "第 1 步 PowerShell 命令") is { IsVisible: true },
                "Operation progress was hidden while TTS was pending");
            Require(!Output(window).Contains("RESULT-42") && !VisibleReply(window).Contains("检查完毕") && played == 0,
                "An answer or operation result was displayed before TTS arrived");
            var clip1 = new TestClip();
            if (mode == 1)
            {
                interaction.Cancel(); first.TrySetResult(clip1);
                await Until(() => clip1.Released);
                Require(!window.IsVisible && played == 0 && !interaction.HistoryText.Contains("检查完毕"),
                    "Cancelled generation resurrected the answer or playback");
                return;
            }
            first.SetResult(clip1);
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => VisibleReply(window).Contains("检查完毕"));
            Require(Output(window).Contains("RESULT-42") && played == 1 && !second.Task.IsCompleted,
                "A playable first clip did not release results while later synthesis was pending");
            var clip2 = new TestClip(); second.SetResult(clip2);
            await Until(() => VisibleReply(window).Contains("检查完毕") && played == 2);
            Require(Output(window).Contains("RESULT-42") && clip1.Released && clip2.Released,
                "Complete audio did not release both the response and command result");
        }
        finally
        {
            first.TrySetCanceled(); second.TrySetCanceled();
            interaction.Dispose(); await interaction.ResourceDisposal;
        }
    }

    private static string Output(Window window) => Find<TextBox>(window,
        x => AutomationProperties.GetName(x) == "第 1 步返回结果")?.Text ?? "";
    private static string VisibleReply(Window window)
    {
        var box = Find<RichTextBox>(window, _ => true);
        return box is null ? "" : new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
    }
    private static T? Find<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        if (node is T value && match(value)) return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i), match) is { } child) return child;
        return null;
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(50); }
        throw new InvalidOperationException("Speech display timing did not reach the expected state");
    }
    private sealed class TestClip() : OwnedPcmStream(new MemoryStream(new byte[800 * 4]),
        WaveFormat.CreateIeeeFloatWaveFormat(8000, 1))
    {
        internal bool Released;
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }
    }
    private sealed class ReplyTransport : HttpMessageHandler
    {
        private int turn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool tool = turn++ == 0;
            object message = tool ? new { tool_calls = new[] { new { id = "speech-timing", type = "function",
                function = new { name = "run_powershell", arguments = JsonSerializer.Serialize(new
                { command = "Write-Output 'RESULT-42'", description = "读取测试结果" }) } } } }
                : new { content = "检查完毕。语音已经准备好。" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                JsonSerializer.Serialize(new { choices = new[] { new { message, finish_reason = tool ? "tool_calls" : "stop" } } }),
                Encoding.UTF8, "application/json") });
        }
    }
}
