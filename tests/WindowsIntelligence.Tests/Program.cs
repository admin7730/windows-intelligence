using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using NAudio.Wave;
using WindowsIntelligence;
using Windows.Media.Core;
using Windows.Media.Playback;
using Screen = System.Windows.Forms.Screen;

internal static class Program
{
    private static int passed, failed;

    [STAThread]
    public static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) =>
        {
            if (args.Contains("--powershell") || args.Contains("--powershell-live"))
            {
                if (args.Contains("--powershell-live"))
                    await Check("Configured AI service and local PowerShell round trip", LivePowerShellTest);
                else
                    await Check("Autonomous PowerShell, file I/O and error recovery", PowerShellTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--asr"))
            {
                await Check("Qwen3-ASR final accuracy and speech detection", FinalAsrTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--minimax-live"))
            {
                await Check("Configured MiniMax endpoint returns decodable audio", async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
                    var settings = Settings.Load();
                    Require(settings.Tts == TtsMode.MiniMax, "MiniMax must be the configured default");
                    using var audio = await MiniMaxSpeech.CreateAsync("你好，语音测试。", settings, timeout.Token);
                    Require(audio.TotalTime.TotalSeconds > .2, "cloud audio was empty");
                    Console.WriteLine($"Decoded {audio.TotalTime.TotalSeconds:F2}s of speech.");
                });
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--assistant"))
            {
                await Check("Background tool window and rendered Markdown", AssistantTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--voice-submit"))
            {
                await Check("Automatic final speech submission", VoiceSubmissionTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--hotkey"))
            {
                await Check("Windows global assistant shortcut and fallback", GlobalShortcutTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--overlay"))
            {
                await Check("DirectComposition presentation and foreground hit testing", OverlayTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            if (args.Contains("--settings-ui"))
            {
                await Check("Settings navigation and microphone controls", SettingsVisualTest);
                application.Shutdown(failed == 0 ? 0 : 1);
                return;
            }
            await Check("Copilot URI and event gate", ActivationTest);
            await Check("Windows global assistant shortcut and fallback", GlobalShortcutTest);
            await Check("Shot state transitions and duplicate submission", StateTest);
            await Check("Multimodal request, SSE and vision capability", ApiTest);
            await Check("Autonomous PowerShell, file I/O and error recovery", PowerShellTest);
            await Check("Credential Manager round trip", CredentialsTest);
            await Check("Local streaming ASR, Chinese and English", AsrTest);
            await Check("Qwen3-ASR final accuracy and speech detection", FinalAsrTest);
            await Check("Local TTS PCM plus Windows device playback", TtsTest);
            await Check("Cloud speech request contracts and MiniMax failover", CloudSpeechTest);
            await Check("Graphics Capture window and real monitor / own UI exclusion", CaptureTest);
            await Check("DirectComposition presentation and foreground hit testing", OverlayTest);
            await Check("Assistant window and rendered Markdown", AssistantTest);
            await Check("Automatic final speech submission", VoiceSubmissionTest);
            await Check("Assistant-only settings compatibility", SettingsTest);
            await Check("Settings navigation and microphone controls", SettingsVisualTest);
            Console.WriteLine($"{passed} passed / {failed} failed");
            application.Shutdown(failed == 0 ? 0 : 1);
        };
        return application.Run();
    }

    private static async Task Check(string name, Func<Task> run)
    {
        try { await run(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static void Require(bool condition, string text)
    {
        if (!condition) throw new InvalidOperationException(text);
    }

    private static Task ActivationTest()
    {
        Require(Copilot.Parse("windowsintelligence-copilot://?state=Down") == Activation.Down, "Down URI");
        Require(Copilot.Parse("windowsintelligence-copilot://?state=Up") == Activation.Up, "Up URI");
        Require(Copilot.Parse("windowsintelligence-copilot://?state=Tap") == Activation.Tap, "Tap URI");
        Require(Copilot.Parse("windowsintelligence-copilot://example?state=Tap") is null, "foreign host");
        var gate = new CopilotGate();
        Require(gate.Resolve(Activation.Tap, 0, fastPath: true) == Activation.Tap &&
            gate.Resolve(Activation.Tap, 110) is null, "cross-source Tap deduplication");
        Require(gate.Resolve(Activation.Tap, 235, fastPath: true) == Activation.SilentTap &&
            gate.Resolve(Activation.Tap, 260) is null, "second Copilot press should open silent mode once");
        Require(gate.Resolve(Activation.Down, 400) == Activation.Down && gate.Resolve(Activation.Down, 410) is null, "Hold deduplication");
        Require(gate.Resolve(Activation.Tap, 500) is null, "Tap during hold");
        Require(gate.Resolve(Activation.Up, 550) == Activation.Up && gate.Resolve(Activation.Up, 600) is null, "Release deduplication");
        Require(gate.Resolve(Activation.Tap, 700) == Activation.Tap && gate.Resolve(Activation.Tap, 1250) == Activation.Tap,
            "independent single presses");
        return Task.CompletedTask;
    }

    private static Task StateTest()
    {
        var state = new ScreenshotState();
        foreach (var step in new[] { ScreenshotPhase.Triggered, ScreenshotPhase.Capturing, ScreenshotPhase.Captured, ScreenshotPhase.Preview,
            ScreenshotPhase.WaitingForSpeech, ScreenshotPhase.Listening, ScreenshotPhase.Transcribing, ScreenshotPhase.ReadyToSend, ScreenshotPhase.Uploading, ScreenshotPhase.AiProcessing })
            Require(state.Transition(step), $"transition {step}");
        Require(!state.Transition(ScreenshotPhase.Uploading), "duplicate upload");
        Require(state.Transition(ScreenshotPhase.Failed) && state.Retry() && state.Transition(ScreenshotPhase.Uploading), "explicit retry");
        Require(state.Transition(ScreenshotPhase.Completed) && !state.Retry(), "complete");
        Require(new ScreenshotState().RequestId != state.RequestId, "unique IDs");
        return Task.CompletedTask;
    }

    private static async Task GlobalShortcutTest()
    {
        using var receiver = new HwndSource(new HwndSourceParameters("Windows Intelligence shortcut test")
        { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
        using var second = new HwndSource(new HwndSourceParameters("Windows Intelligence shortcut conflict test")
        { WindowStyle = unchecked((int)0x80000000), Width = 1, Height = 1 });
        var events = new List<Activation>();
        using var runtime = new AssistantHotkey(receiver.Handle, events.Add);
        using var blocker = new AssistantHotkey(second.Handle, _ => { });
        receiver.AddHook(Hook);
        Require(runtime.Active is null && new Settings().Shortcut is null, "Copilot must be the default");
        var first = new AssistantShortcut(ModifierKeys.None, 0x87);
        var occupied = new AssistantShortcut(ModifierKeys.None, 0x86);
        var rollback = new AssistantShortcut(ModifierKeys.None, 0x85);
        var conventional = new AssistantShortcut(ModifierKeys.Control | ModifierKeys.Alt, 0x20);
        Require(conventional.IsValid && conventional.ToString() == "Ctrl + Alt + Space", "modifier chord format");
        Require(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { Shortcut = conventional }))?.Shortcut == conventional,
            "custom chord did not survive settings round trip");
        runtime.Apply(first);
        blocker.Apply(occupied);
        try { runtime.Apply(occupied); throw new InvalidOperationException("duplicate Windows hotkey registration was accepted"); }
        catch (UserError) { Require(runtime.Active == first, "shortcut conflict displaced the active key"); }
        try { runtime.Apply(rollback, () => throw new IOException("settings failed")); }
        catch (IOException) { Require(runtime.Active == first, "failed persistence displaced the active key"); }
        blocker.Apply(rollback); // A failed persistence callback must have released the attempted registration.

        try
        {
            KeybdEvent(0x87, 0, 0, 0);
            await Task.Delay(65);
            KeybdEvent(0x87, 0, 2, 0);
            await Until(() => events.Count == 1, "Windows did not deliver the custom tap");
            Require(events[0] == Activation.Tap, "a short shortcut press did not produce a tap");
            KeybdEvent(0x87, 0, 0, 0);
            await Until(() => events.Count >= 2, "Windows did not deliver the custom hold");
            Require(events[1] == Activation.Down, "a held shortcut did not start the hold gesture");
        }
        finally { KeybdEvent(0x87, 0, 2, 0); }
        await Until(() => events.Count == 3, "Windows did not deliver the custom release");
        Require(events.SequenceEqual([Activation.Tap, Activation.Down, Activation.Up]), "unexpected custom shortcut events");
        runtime.Apply(null);
        Require(runtime.Active is null, "failed to restore the Copilot default");
        blocker.Apply(first);

        nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == AssistantHotkey.WindowMessage) handled = runtime.HandleMessage((int)wParam);
            return 0;
        }
        static async Task Until(Func<bool> state, string failure)
        {
            for (int i = 0; i < 70 && !state(); i++) await Task.Delay(30);
            Require(state(), failure);
        }
    }

    private static BitmapSource BitmapFixture() =>
        BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16);

    private static async Task ApiTest()
    {
        var local = new Settings();
        local.Provider.BaseUrl = "http://127.0.0.1:11434/v1";
        var pixels = BitmapFixture();
        var image = ScreenCapture.Encode(pixels, "PNG", 90, 1200);
        var request = MultimodalRequestBuilder.Build(Guid.NewGuid(), local, "解释报错", image, []);
        var handler = new Fake();
        using var http = new HttpClient(handler);
        var output = new System.Collections.Generic.List<string>();
        var answer = await new AiClient(http).ReplyAsync(request, "mock", output.Add, CancellationToken.None);
        Require(answer == "第一段。第二段。" && output.Count == 2, "incremental SSE result");
        Require(handler.Count == 1, "exactly one successful API request");
        local.Provider.SupportsVision = false;
        try { MultimodalRequestBuilder.Build(Guid.NewGuid(), local, "", image, []); throw new InvalidOperationException("vision was accepted"); }
        catch (UserError) { }
        using var broken = new MemoryStream(Encoding.UTF8.GetBytes("data: {}\n\n"));
        try { await AiClient.ReadEventsAsync(broken, _ => { }, CancellationToken.None); throw new InvalidOperationException("accepted terminated SSE"); }
        catch (UserError) { }
    }

    private sealed class Fake : HttpMessageHandler
    {
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            Require(request.RequestUri?.AbsolutePath == "/v1/chat/completions", "completion endpoint");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var user = json.RootElement.GetProperty("messages").EnumerateArray().Last();
            var parts = user.GetProperty("content");
            Require(parts.GetArrayLength() == 2 && parts[0].GetProperty("text").GetString() == "解释报错", "same user message");
            var content = parts[1].GetProperty("image_url");
            var image = content.GetProperty("url").GetString()!;
            byte[] bytes = Convert.FromBase64String(image.Split(',')[1]);
            Require(image.StartsWith("data:image/png;base64,") && bytes[0] == 137 && bytes[1] == 80, "valid image bytes");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(": start\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"第一段。\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"第二段。\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private static async Task PowerShellTest()
    {
        var settings = new Settings();
        settings.Provider.BaseUrl = "http://127.0.0.1:11434/v1";
        var folder = Directory.CreateTempSubdirectory("wi-powershell-");
        string file = Path.Combine(folder.FullName, "操作测试.txt");
        const string content = "中文自动操作测试";
        string literal = "'" + file.Replace("'", "''") + "'";
        string read = $"Get-Content -LiteralPath {literal} -Encoding UTF8";
        string[] commands = [read, $"Set-Content -LiteralPath {literal} -Value '{content}' -Encoding UTF8", read];
        try
        {
            var request = MultimodalRequestBuilder.Build(Guid.NewGuid(), settings, $"检查 {file}，没有的话写入“{content}”，然后读给我。", null, []);
            var handler = new PowerShellTransport(commands, content);
            using var http = new HttpClient(handler);
            var output = new List<string>();
            int executions = 0;
            var answer = await new AiClient(http).ReplyAsync(request, "", output.Add, CancellationToken.None, async (command, ct) =>
            {
                Require(command == commands[executions], "streamed arguments were not reassembled or operations were out of order");
                executions++;
                return await PowerShellRunner.RunAsync(command, ct);
            });
            Require(answer == $"已写入并核对：{content}。" && output.SequenceEqual([answer]) &&
                executions == 3 && handler.Count == 4 && File.ReadAllText(file).Trim() == content,
                "missing file operation, recovery, or final response");
        }
        finally
        {
            File.Delete(file);
            folder.Delete();
        }

        var error = await PowerShellRunner.RunAsync("throw '中文错误'", CancellationToken.None);
        Require(error.ExitCode == 1 && error.Error.Contains("中文错误"), "nonzero exit code and UTF-8 stderr");
        var native = await PowerShellRunner.RunAsync("cmd.exe /c exit 7", CancellationToken.None);
        Require(native.ExitCode == 7, "native process exit code was dropped");
        var bounded = await PowerShellRunner.RunAsync("Write-Output ('中' * 10000)", CancellationToken.None);
        Require(bounded.ExitCode == 0 && bounded.Output.Length < 6100 && bounded.Output.StartsWith("中") &&
            bounded.Output.Contains("输出已截断"), "UTF-8 output was not bounded");
        var timeout = await PowerShellRunner.RunAsync("Start-Sleep -Seconds 20", CancellationToken.None, TimeSpan.FromSeconds(1));
        Require(timeout.TimedOut && timeout.ExitCode is null, "command did not time out");
    }

    private static async Task LivePowerShellTest()
    {
        var settings = Settings.Load();
        Require(settings.PowerShellEnabled && settings.Provider.SupportsTools, "PowerShell or model tool calls are disabled");
        const string command = "Write-Output (17 * 19)";
        var request = MultimodalRequestBuilder.Build(Guid.NewGuid(), settings,
            $"请在本机实际执行 PowerShell 命令 `{command}`，原样调用工具，然后告诉我结果。", null, []);
        bool executed = false;
        var answer = await new AiClient().ReplyAsync(request, SecretStore.Get(request.Provider.Id), _ => { }, CancellationToken.None, async (script, ct) =>
        {
            Require(script.Trim() == command, "live service did not propose the exact harmless test command");
            var result = await PowerShellRunner.RunAsync(script, ct);
            executed = result.ExitCode == 0 && result.Output.Trim() == "323";
            return result;
        });
        Require(executed && answer.Contains("323") && !answer.StartsWith("PowerShell 已退出"), "live service did not summarize the command result");
    }

    private sealed class PowerShellTransport(string[] commands, string content) : HttpMessageHandler
    {
        public int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            int step = Count++;
            Require(body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "run_powershell",
                "PowerShell tool was not advertised for this operation");
            if (step > 0)
            {
                var messages = body.GetProperty("messages");
                var assistant = messages[messages.GetArrayLength() - 2];
                var tool = messages[messages.GetArrayLength() - 1];
                var id = $"ps-{step - 1}";
                Require(assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString() == id &&
                    tool.GetProperty("role").GetString() == "tool" && tool.GetProperty("tool_call_id").GetString() == id, "tool response linkage");
                using var result = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
                Require(result.RootElement.GetProperty("exitCode").GetInt32() == (step == 1 ? 1 : 0),
                    "the model did not receive the real exit code");
                if (step == 3) Require(result.RootElement.GetProperty("output").GetString()?.Trim() == content, "bad file content");
            }

            var events = new StringBuilder();
            void Frame(object delta, string? finish = null) =>
                events.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } })).Append("\n\n");
            if (step < commands.Length)
            {
                string arguments = JsonSerializer.Serialize(new { command = commands[step] });
                int split = arguments.Length / 2;
                Frame(new
                {
                    content = "我会执行一堆命令。",
                    tool_calls = new[] { new { index = 0, id = $"ps-{step}", type = "function",
                        function = new { name = "run_powershell", arguments = arguments[..split] } } }
                });
                Frame(new { tool_calls = new[] { new { index = 0, function = new { arguments = arguments[split..] } } } }, "tool_calls");
            }
            else Frame(new { content = $"已写入并核对：{content}。" }, "stop");
            events.Append("data: [DONE]\n\n");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(events.ToString(), Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private static Task CredentialsTest()
    {
        var id = Guid.NewGuid();
        try { SecretStore.Set(id, "fixture-口令"); Require(SecretStore.Get(id) == "fixture-口令", "credential differs"); }
        finally { SecretStore.Set(id, ""); }
        return Task.CompletedTask;
    }

    private static async Task AsrTest()
    {
        await Task.Run(() =>
        {
            using var api = new SpeechRecognizer();
            var model = api.Prepare();
            var results = Directory.GetFiles(".tooling/asr-fixtures", "[0-3].wav");
            if (results.Length == 0) results = Directory.GetFiles(".tooling/asr-fixtures", "?.wav");
            foreach (string file in results.Order())
            {
                using var wave = new WaveFileReader(file);
                var reader = wave.ToSampleProvider();
                using var stream = model.CreateStream();
                float[] buffer = new float[960];
                int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stream.AcceptWaveform(wave.WaveFormat.SampleRate, buffer[..n]);
                    while (model.IsReady(stream)) model.Decode(stream);
                }
                stream.AcceptWaveform(16000, new float[8000]); stream.InputFinished();
                while (model.IsReady(stream)) model.Decode(stream);
                var text = model.GetResult(stream).Text;
                Console.WriteLine($"ASR {Path.GetFileName(file)}: {text}");
                Require(text.Any(char.IsLetter), "transcript empty");
            }
            Require(results.Length >= 2, "missing upstream ASR WAV fixtures");
        });
    }

    private static async Task TtsTest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        float max = 0;
        await new SpeechOutput().SpeakAsync("你好，Windows Intelligence。测试语音。", new Settings { Tts = TtsMode.Local }, value => max = Math.Max(max, value), timeout.Token);
        Require(max > .035, "empty audio playback amplitude");
    }

    private static async Task FinalAsrTest()
    {
        using var api = new SpeechRecognizer();
        using var previous = new SpeechRecognizer();
        Require(previous.PrepareFinal() is not null, "SenseVoice model was omitted from the build");
        Require(SpeechRecognizer.HasAccurateModel, "Qwen3-ASR model was omitted from the build");
        var references = new (string Folder, string File, string Text)[]
        {
            ("sensevoice-fixtures", "zh.wav", "开放时间早上九点至下午五点"),
            ("sensevoice-fixtures", "en.wav", "The tribal chieftain called for the boy and presented him with fifty pieces of gold"),
            ("qwen-asr-fixtures", "f1_noise.wav", "Okay, Charles. It looks like we have a problem with the radio. What happened? Yeah, someone spilled water on their machine. I uh, yeah. Charles, can you hear us? Mamma mia.")
        };
        int onlineErrors = 0, senseErrors = 0, finalErrors = 0, total = 0;
        foreach (var (folder, file, expected) in references)
        {
            var path = Path.Combine(".tooling", folder, file);
            using var wave = new WaveFileReader(path);
            var reader = wave.ToSampleProvider();
            var samples = new float[checked((int)wave.SampleCount)];
            int length = reader.Read(samples, 0, samples.Length);
            var online = api.Prepare();
            using var stream = online.CreateStream();
            stream.AcceptWaveform(16000, samples[..length]);
            stream.AcceptWaveform(16000, new float[8000]);
            stream.InputFinished();
            while (online.IsReady(stream)) online.Decode(stream);
            var baseline = online.GetResult(stream).Text;
            string oldFinal = previous.FinalizeUtterance(samples[..length], "", CancellationToken.None, accurate: false);
            var time = Stopwatch.StartNew();
            var final = await api.TranscribeFileAsync(path, _ => { }, CancellationToken.None);
            var reference = Normalize(expected);
            onlineErrors += Distance(reference, Normalize(baseline));
            senseErrors += Distance(reference, Normalize(oldFinal));
            finalErrors += Distance(reference, Normalize(final));
            total += reference.Length;
            Console.WriteLine($"ASR {file}: SenseVoice='{oldFinal}' Qwen3-ASR='{final}' ({time.ElapsedMilliseconds} ms)");
            if (file == "zh.wav")
            {
                using var activity = new VoiceActivity();
                var quietRecording = new List<float>();
                var signal = new float[16000].Concat(samples[..length].Select(x => x * .35f)).Concat(new float[32000]);
                foreach (float[] block in signal.Chunk(512))
                {
                    quietRecording.AddRange(block);
                    activity.Observe(block, quietRecording.Count / 16);
                    if (quietRecording.Count < 16000) Require(!activity.Started, "silence was identified as speech");
                }
                var utterance = activity.Utterance(quietRecording);
                Require(activity.Started && utterance.Length < quietRecording.Count - 16000, "quiet speech detection or silence trimming failed");
                var quiet = api.FinalizeUtterance(utterance, "", CancellationToken.None);
                Console.WriteLine($"Quiet sample: '{quiet}'");
                Require(Distance(reference, Normalize(quiet)) <= 1, "quiet clipped speech lost words");
            }
        }
        var yue = await api.TranscribeFileAsync(Path.Combine(".tooling", "sensevoice-fixtures", "yue.wav"), _ => { }, CancellationToken.None);
        Require(yue.Count(char.IsLetterOrDigit) >= 6, "Cantonese emitted too little speech");
        Console.WriteLine($"ASR yue.wav: final='{yue}'");
        Console.WriteLine($"Character errors on annotated samples: Zipformer {onlineErrors}/{total}, SenseVoice {senseErrors}/{total}, Qwen3-ASR {finalErrors}/{total}.");
        Require(finalErrors < onlineErrors && finalErrors <= senseErrors, "final recognition regressed on annotated samples");

        static string Normalize(string text) => new(text.ToLowerInvariant().Replace("fifty", "50")
            .Replace('開', '开').Replace('時', '时').Replace('間', '间').Replace('點', '点')
            .Replace('九', '9').Replace('五', '5').Where(char.IsLetterOrDigit).ToArray());
        static int Distance(string reference, string result)
        {
            var row = Enumerable.Range(0, result.Length + 1).ToArray();
            for (int i = 1; i <= reference.Length; i++)
            {
                int previous = row[0]++;
                for (int j = 1; j <= result.Length; j++)
                {
                    int next = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (reference[i - 1] == result[j - 1] ? 0 : 1));
                    previous = row[j];
                    row[j] = next;
                }
            }
            return row[^1];
        }
    }

    private static async Task CloudSpeechTest()
    {
        var models = new List<string>();
        using var miniMax = new HttpClient(new SpeechMock(async (request, ct) =>
        {
            Require(request.RequestUri?.AbsoluteUri == "https://api.minimaxi.com/v1/t2a_v2" &&
                request.Headers.Authorization?.Parameter == "test-key", "wrong MiniMax endpoint or authentication");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            models.Add(body.RootElement.GetProperty("model").GetString()!);
            return models.Count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"base_resp\":{\"status_code\":1008}}", Encoding.UTF8, "application/json") };
        }));
        try { using var audio = await MiniMaxSpeech.CreateAsync("测试", new Settings(), CancellationToken.None, miniMax, "test-key"); }
        catch (UserError error) { Require(error.Message.Contains("503") && error.Message.Contains("1008"), "provider errors lost"); }
        Require(models.SequenceEqual([MiniMaxSpeech.HighDefinition, MiniMaxSpeech.Turbo]), "MiniMax fallback order wrong");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { using var audio = await MiniMaxSpeech.CreateAsync("测试", new Settings(), cancel.Token, miniMax, "test-key"); }
        catch (OperationCanceledException) { }
        Require(models.Count == 2, "cancelled synthesis reached provider");

        bool called = false;
        using var elevenLabs = new HttpClient(new SpeechMock(async (request, ct) =>
        {
            called = true;
            Require(request.RequestUri?.Host == "api.elevenlabs.io" && request.RequestUri.AbsolutePath.StartsWith("/v1/text-to-speech/") &&
                request.Headers.GetValues("xi-api-key").Single() == "test-key", "wrong ElevenLabs endpoint or authentication");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Require(body.RootElement.GetProperty("model_id").GetString() == "eleven_flash_v2_5", "wrong ElevenLabs model");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }));
        try { using var audio = await ElevenLabsSpeech.CreateAsync("测试", new Settings(), CancellationToken.None, elevenLabs, "test-key"); }
        catch (UserError error) { Require(error.Message.Contains("凭据"), "authentication error was not surfaced"); }
        Require(called, "ElevenLabs request was not sent");
    }

    private sealed class SpeechMock(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }


    private static async Task CaptureTest()
    {
        var fixture = new Window { Title = "Windows Intelligence · Capture fixture", Background = Brushes.Lime, Left = 70, Top = 70,
            Width = 400, Height = 240, Topmost = true, WindowStyle = WindowStyle.None };
        fixture.Show();
        try
        {
            var h = new WindowInteropHelper(fixture).Handle;
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var screen = Screen.PrimaryScreen!;
            var bounds = screen.Bounds;
            Desktop.SetWindowPos(h, -1, bounds.Left + 220, bounds.Top + 170, 420, 260, 0x40);
            await Task.Delay(180);
            await Task.Run(Desktop.DwmFlush);
            var window = await ScreenCapture.CaptureAsync(CaptureTarget.ActiveWindow, screen, h, ct.Token);
            Require(IsGreen(window, window.PixelWidth / 2, window.PixelHeight / 2), "fixture window pixels differ");
            const int x = 425, y = 300;
            var included = await ScreenCapture.CaptureAsync(CaptureTarget.CurrentMonitor, screen, h, ct.Token);
            Require(IsGreen(included, x, y), "monitor did not contain fixture");
            Require(Desktop.SetWindowDisplayAffinity(h, 0x11), "could not set affinity");
            await Task.Run(Desktop.DwmFlush);
            var excluded = await ScreenCapture.CaptureAsync(CaptureTarget.CurrentMonitor, screen, h, ct.Token);
            Require(!IsGreen(excluded, x, y), "excluded fixture leaked");
            Console.WriteLine($"Capture {included.PixelWidth}x{included.PixelHeight}, tested affinity at {x},{y}");
        }
        finally { fixture.Close(); }
    }

    private static bool IsGreen(System.Windows.Media.Imaging.BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[1] > 240 && pixel[0] < 15 && pixel[2] < 15;
    }

    private static async Task OverlayTest()
    {
        var screen = Screen.PrimaryScreen!;
        var fixture = new Window { Width = 1300, Height = 820, Left = 35, Top = 15, Background = Brushes.White, WindowStyle = WindowStyle.None, Topmost = true };
        fixture.Show();
        try
        {
            var handle = new WindowInteropHelper(fixture).Handle;
            var b = screen.Bounds;
            Desktop.SetWindowPos(handle, -1, b.Left + 10, b.Top + 10, b.Width - 20, b.Height - 20, 0x40);
            await Task.Delay(180);
            var point = new System.Drawing.Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
            var before = WindowFromPoint(point);
            Require(before != 0, "no baseline window at mouse point");
            using var render = new Overlay(new Settings { OrbPosition = 1 });
            render.SetActive(screen, Mood.Listening);
            render.Energy = .48f;
            var clock = Stopwatch.StartNew();
            await Task.Delay(1150);
            double fps = render.Frames / clock.Elapsed.TotalSeconds;
            Require(render.Ready && fps > 15, render.Failure ?? $"Present rate {fps:F1}");
            double megabytes = render.TransferBytes / Math.Max(1d, render.Frames) / 1_000_000;
            double full = (double)b.Width * b.Height * 4 / 1_000_000;
            Require(megabytes > .05 && megabytes < full * .65, "regional readback not in use");
            uint firstEdge = render.ShaderPixel(screen, b.Width / 2, 4);
            var hit = WindowFromPoint(point);
            var hwnd = render.WindowFor(screen);
            Require(hit != 0 && GetAncestor(hit, 2) != hwnd, $"overlay interferes with pointer hit testing (from {Title(before)} to {Title(hit)}, overlay {hwnd:X})");
            Require(hwnd != 0 && Desktop.SetWindowDisplayAffinity(hwnd, 0), "test overlay could not be made capturable");
            Desktop.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x13);
            await Task.Delay(170);
            uint laterEdge = render.ShaderPixel(screen, b.Width / 2, 4);
            Require((firstEdge >> 24) > 0 && (laterEdge >> 24) > 0, "edge light disappeared after opening");
            foreach (int channel in new[] { 0, 8, 16 })
            {
                double firstRatio = ((firstEdge >> channel) & 255) / (double)(firstEdge >> 24);
                double laterRatio = ((laterEdge >> channel) & 255) / (double)(laterEdge >> 24);
                Require(Math.Abs(firstRatio - laterRatio) < .06, "edge palette shifted while breathing");
            }
            uint[] edgeColors =
            [
                render.ShaderPixel(screen, b.Width * 9 / 10, 3),
                render.ShaderPixel(screen, b.Width - 4, b.Height / 2),
                render.ShaderPixel(screen, b.Width / 2, b.Height - 4),
                render.ShaderPixel(screen, 3, b.Height / 2),
                render.ShaderPixel(screen, b.Width / 9, 3)
            ];
            Require(edgeColors.All(pixel => (pixel >> 24) > 48), "edge rim is too faint");
            Require(edgeColors.Any(pixel => Red(pixel) > Blue(pixel) + 26) &&
                edgeColors.Any(pixel => Green(pixel) > Red(pixel) + 26) &&
                edgeColors.Any(pixel => Blue(pixel) > Red(pixel) + 26), "edge lacks warm and cool accent colors");
            uint haloEdge = render.ShaderPixel(screen, b.Width / 2, 27);
            Require((haloEdge >> 24) > 3 && (laterEdge >> 24) > (haloEdge >> 24) * 2, "edge lacks a soft falloff behind the rim");
            Console.WriteLine($"Edge color samples: {string.Join(", ", edgeColors.Select(pixel => pixel.ToString("X8")))}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            var screenshot = await ScreenCapture.CaptureAsync(CaptureTarget.CurrentMonitor, screen, handle, timeout.Token);
            var (orbX, orbY, _) = render.Position(screen);
            var orb = Pixel(screenshot, (int)orbX + 23, (int)orbY - 22);
            var top = Pixel(screenshot, screenshot.PixelWidth / 2, 32);
            nint dc = GetDC(0);
            uint desktopPixel = GetPixel(dc, b.Left + (int)orbX + 23, b.Top + (int)orbY - 22);
            ReleaseDC(0, dc);
            Console.WriteLine($"GPU frame {render.ShaderPixel(screen, (int)orbX + 23, (int)orbY - 22):X8} orb; composed {string.Join(',', orb)} orb; GDI {desktopPixel:X8}; backdrop {render.BackdropStatus}.");
            Require(orb.Take(3).Max() - orb.Take(3).Min() > 20, $"orb shader is absent in composed frame: {string.Join(',', orb)}");
            Require(top.Take(3).Min() > 90, "the GPU overlay still draws a separate island");
            Console.WriteLine($"GPU Present calls/s: {fps:F1}; host DPI {Desktop.GetDpiForWindow(handle)}; Host backdrop {render.BackdropStatus}");
            render.Dismiss();
            await Task.Delay(940);
            var frames = render.Frames;
            var transfer = render.TransferBytes;
            await Task.Delay(450);
            Require(render.Frames == frames && render.TransferBytes == transfer, "GPU readback during idle");
            Console.WriteLine($"GPU copied {megabytes:F2} MB/frame vs {full:F2} for full frame; idle copied 0 bytes.");
        }
        finally { fixture.Close(); }

        static uint Red(uint pixel) => (pixel >> 16) & 255;
        static uint Green(uint pixel) => (pixel >> 8) & 255;
        static uint Blue(uint pixel) => pixel & 255;
    }



    private static Task SettingsTest()
    {
        var legacy = JsonSerializer.Deserialize<Settings>("""
            {"Island":false,"IslandAppearance":{"OpenWidth":700},"Media":true,"WritingHotkey":false,"ImageServiceModel":"old-model","Tray":false,"AutoSpeak":false,"Providers":[{"Name":"Test"}]}
            """)!;
        Require(!legacy.Tray && !legacy.AutoSpeak && legacy.Providers[0].Name == "Test", "core settings did not survive legacy JSON");
        Require(!JsonSerializer.Serialize(legacy).Contains("IslandAppearance"), "removed island settings are still serialized");
        Require(!JsonSerializer.Serialize(legacy).Contains("WritingHotkey"), "removed workbench settings are still serialized");
        Require(!JsonSerializer.Serialize(legacy).Contains("ImageServiceModel"), "retired image service settings are still serialized");
        return Task.CompletedTask;
    }

    private static async Task AssistantTest()
    {
        Require(Copilot.FromLaunch([]) == Activation.Background && Copilot.FromLaunch(["--settings"]) == Activation.Open,
            "plain app launch must stay in the background");
        const string sample = """
            ## 文件已准备好

            已生成 **预览**，确认后就可以发送。

            - 检查名称
            - 复核日期

            | 项目 | 状态 |
            | --- | --- |
            | 预览 | 完成 |

            ~~~powershell
            Get-Date
            ~~~

            [查看指南](https://example.com) [本机链接](file:///C:/example.txt)
            """;
        var doc = ReplyMarkdown.Format(sample, light: true);
        Require(doc.Blocks.FirstBlock is Paragraph heading && heading.FontSize > 14 &&
            doc.Blocks.OfType<System.Windows.Documents.List>().Any() && doc.Blocks.OfType<Table>().Any(),
            "Markdown headings, lists or tables were rendered as raw text");
        Require(doc.Blocks.OfType<Paragraph>().Any(p => p.FontFamily.Source.Contains("Cascadia")), "code block missing");
        Require(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).Count() == 1,
            "non-HTTP Markdown link was activated or web link missing");

        using var caption = new Caption();
        caption.State("输入问题", "", "", true, false, "");
        caption.Show();
        var screen = Screen.PrimaryScreen!;
        var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Windows Intelligence · 助手");
        var handle = new WindowInteropHelper(window).Handle;
        Require(Desktop.IsWindowVisible(handle), "assistant HWND stayed hidden after Show");
        var scale = Desktop.GetDpiForWindow(handle) / 96d;
        caption.Place(screen, scale);
        await Task.Delay(100);
        Require(!window.ShowInTaskbar && (GetExtendedStyle(handle, -20).ToInt64() & 0x40080) == 0x80,
            "assistant window can appear in taskbar/Alt+Tab");
        var inputOnly = window.ActualHeight;
        caption.Silent = true;
        caption.State("输入问题", "", "", true, false, "");
        Require(FindButton(window, "静默模式") is { IsVisible: true, IsEnabled: false }, "silent mode exposed a microphone action");
        var prompt = FindNamed<TextBox>(window, "输入问题");
        Require(prompt is not null, "input control missing");
        prompt!.Text = "测试静默输入";
        string? submitted = null;
        caption.Submit += value => submitted = value;
        var submit = FindButton(window, "发送问题");
        Require(submit is { IsEnabled: true, IsVisible: true }, "silent mode lost the submit action");
        submit!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(submitted == "测试静默输入", "silent send did not pass the typed text");
        caption.State("正在操作电脑", "读一下这个文件", "", false, false, "");
        Require(FindButton(window, "运行 PowerShell") is null && FindButton(window, "关闭") is { IsVisible: true },
            "autonomous operation should keep cancellation available without asking for another approval");
        caption.State("回复完成", "", sample, false, false, "");
        await Task.Delay(150);
        Require(window.ActualHeight > inputOnly + 100, "the reply did not open below the input");
        var target = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, "assistant-preview.png"));
        encoder.Save(output);
        bool cancelled = false;
        caption.Cancel += () => { cancelled = true; caption.Hide(); };
        SendMessage(handle, 0x0312, 0x4312, 0x1B);
        Require(cancelled && !window.IsVisible, "Esc did not close the assistant");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetExtendedStyle(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeybdEvent(byte key, byte scan, uint flags, nuint extraInfo);


    private static Button? FindButton(DependencyObject parent, string name) => FindNamed<Button>(parent, name);

    private static async Task VoiceSubmissionTest()
    {
        var model = new Settings { ReduceMotion = true, Cue = false, AutoSpeak = false, SaveTextHistory = false, PowerShellEnabled = false };
        model.Provider.BaseUrl = "http://127.0.0.1:11434/v1";
        model.Provider.Streaming = false;
        var submitted = new List<string>();
        using var http = new HttpClient(new SpeechMock(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            submitted.Add(body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"处理完成。"},"finish_reason":"stop"}]}""",
                Encoding.UTF8, "application/json") };
        }));
        var captures = new List<TaskCompletionSource<string>>();
        Task<string> Capture(Action<string> draft, CancellationToken token)
        {
            var capture = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            captures.Add(capture);
            draft("临时识别");
            return capture.Task.WaitAsync(token);
        }
        using var overlay = new Overlay(model, renderOrb: false);
        using var caption = new Caption();
        using var interaction = new Interaction(model, Dispatcher.CurrentDispatcher, overlay, caption, new AiClient(http), Capture);
        interaction.Start(false);
        await Until(() => captures.Count == 1);
        await Task.Delay(80);
        var window = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Windows Intelligence · 助手");
        var send = FindButton(window, "发送");
        Require(send is null || !send.IsVisible, "voice draft asked the user to submit it");
        send?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(submitted.Count == 0, "partially recognized speech was sent early");
        captures[0].SetResult("  第一条完整语音。  ");
        await Until(() => interaction.HistoryText.Contains("第一条完整语音。"));
        Require(submitted.SequenceEqual(["第一条完整语音。"]), "the final utterance was not sent once and intact");

        interaction.Start(false, typed: true);
        var mic = FindButton(window, "开始语音输入");
        Require(mic is { IsVisible: true, IsEnabled: true }, "assistant microphone button is unavailable");
        mic!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => captures.Count == 2);
        captures[1].SetResult("第二条完整语音。");
        await Until(() => interaction.HistoryText.Contains("第二条完整语音。"));
        Require(submitted.SequenceEqual(["第一条完整语音。", "第二条完整语音。"]), "microphone entry did not send speech automatically");

        interaction.Start(false);
        await Until(() => captures.Count == 3);
        interaction.Cancel();
        captures[2].SetResult("已取消的语音。");
        await Task.Delay(120);
        Require(submitted.Count == 2, "cancelled speech was submitted");

        static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition()) return;
                await Task.Delay(30);
            }
            throw new InvalidOperationException("The voice interaction did not reach the expected state.");
        }
    }

    private static async Task SettingsVisualTest()
    {
        var model = new Settings { ReduceMotion = true };
        using var overlay = new Overlay(model, renderOrb: false);
        using var caption = new Caption();
        using var interaction = new Interaction(model, Dispatcher.CurrentDispatcher, overlay, caption);
        var window = new SettingsWindow(model, interaction, overlay, 0);
        window.Show();
        try
        {
            await Task.Delay(120);
            Require(FindButton(window, "输入问题") is { IsVisible: true }, "overview lost the typed assistant entry");
            Snapshot("settings-overview.png");
            var speech = FindNamed<ListBoxItem>(window, "语音识别");
            Require(speech is not null, "speech settings navigation missing");
            speech!.IsSelected = true;
            await Task.Delay(100);
            Require(FindNamed<ComboBox>(window, "麦克风") is { Items.Count: > 0 } &&
                FindNamed<CheckBox>(window, "高准确率复核") is { IsChecked: true } &&
                FindButton(window, "开始试录") is { IsVisible: true }, "speech controls are unavailable");
            Snapshot("settings-speech.png");
            var shortcuts = FindNamed<ListBoxItem>(window, "唤醒快捷键");
            Require(shortcuts is not null, "shortcut settings navigation missing");
            shortcuts!.IsSelected = true;
            await Task.Delay(100);
            Require(FindButton(window, "录制快捷键") is { IsVisible: true } &&
                FindButton(window, "恢复 Copilot 键") is { IsVisible: true }, "shortcut configuration controls missing");
            Snapshot("settings-shortcut.png");
        }
        finally { window.Close(); }

        void Snapshot(string name)
        {
            var visual = (FrameworkElement)window.Content;
            var target = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name));
            encoder.Save(file);
        }
    }

    private static T? FindNamed<T>(DependencyObject parent, string name) where T : DependencyObject
    {
        if (parent is T found && (AutomationProperties.GetName(parent) == name || parent is Button { Content: string label } && label == name))
            return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindNamed<T>(VisualTreeHelper.GetChild(parent, i), name) is { } child) return child;
        return null;
    }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(System.Drawing.Point p);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref System.Drawing.Point point);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out PixelRect rect);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    private static string Title(nint hwnd) { var title = new StringBuilder(180); GetWindowText(hwnd, title, title.Capacity); return $"{hwnd:X} {title}"; }
    private static byte[] Pixel(System.Windows.Media.Imaging.BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return bytes;
    }
}
