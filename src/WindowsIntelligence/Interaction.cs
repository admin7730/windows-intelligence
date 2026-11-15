using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace WindowsIntelligence;

internal sealed class Interaction : IDisposable
{
    private sealed class Session(Screen screen, bool capture, bool canAutoSend, bool silent)
    {
        public readonly Screen Screen = screen;
        public readonly bool CanAutoSend = canAutoSend;
        public readonly bool Silent = silent;
        public readonly ScreenshotState? State = capture ? new() : null;
        public readonly Guid TextId = Guid.NewGuid();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly ReleaseSignal Release = new();
        public readonly CancellationTokenSource Life = new(), Voice = new();
        public BitmapSource? Image;
        public string Input = "", VoiceDraft = "", Status = "", Answer = "";
        public bool Ready, Sending, Failed, Editing, CaptureFailed, VoiceFailed, VoicePending, AutomaticSubmissionPending;
        public volatile bool SpeechStarted;
        public Guid Id => State?.RequestId ?? TextId;
    }

    private readonly Settings settings;
    private readonly Dispatcher dispatcher;
    private readonly Overlay overlay;
    private readonly Caption caption;
    private readonly SpeechRecognizer recognition = new();
    private readonly SpeechOutput speech = new();
    private readonly AiClient ai;
    private readonly Func<Action<string>, CancellationToken, Task<string>>? captureSpeech;
    private readonly SemaphoreSlim captureLock = new(1);
    private readonly SemaphoreSlim micLock = new(1);
    private readonly List<AiTurn> history;
    private CancellationTokenSource? previewSpeech;
    private Session? current;
    private float audioLevel;
    private bool disposed;

    public Interaction(Settings settings, Dispatcher dispatcher, Overlay overlay, Caption caption, AiClient? ai = null,
        Func<Action<string>, CancellationToken, Task<string>>? captureSpeech = null)
    {
        this.settings = settings; this.dispatcher = dispatcher; this.overlay = overlay; this.caption = caption;
        this.ai = ai ?? new();
        this.captureSpeech = captureSpeech;
        history = LoadHistory();
        caption.Submit += text => Run(SubmitAsync(text, false));
        caption.Cancel += () => Cancel(animate: true);
        caption.Speak += StartVoiceFromPanel;
        caption.FollowUp += FollowUp;
        caption.Retry += () => Run(RetryAsync());
        caption.OpenImage += () => Run(EditAsync(false));
        overlay.Place += (screen, x, y, scale) => { overlay.Energy = Volatile.Read(ref audioLevel); caption.Place(screen, scale); };
        if (captureSpeech is null) _ = Task.Run(() => { try { recognition.Prepare(); } catch (Exception) { } });
    }

    public string HistoryText => string.Join("\n\n", history.Select(t => $"你\n{t.Text}\n\nWindows Intelligence\n{t.Answer}"));
    private static string HistoryFile => Path.Combine(Settings.DirectoryPath, "history.json");

    public void Start(bool capture, bool held = false, bool typed = false, bool canAutoSend = true, bool silent = false)
    {
        var screen = settings.Target == CaptureTarget.PrimaryMonitor && capture ? Screen.PrimaryScreen ?? Desktop.CursorScreen : Desktop.CursorScreen;
        nint foreground = Desktop.GetForegroundWindow();
        Cancel();
        var session = current = new Session(screen, capture, canAutoSend, silent);
        session.VoicePending = !typed && !silent && settings.MicrophoneEnabled && (!capture || held || !settings.Strict);
        caption.ReduceMotion = settings.ReduceMotion;
        caption.Silent = session.Silent;
        if (capture && !held) session.Release.Mark(0);
        caption.ResetPrompt();
        if (!capture)
        {
            overlay.SetActive(screen, Mood.Listening);
            session.Status = session.VoicePending ? "正在聆听" : "输入问题";
            session.Ready = true;
            Refresh(session);
            if (typed) caption.FocusPrompt();
        }
        if (settings.Cue && !session.Silent) Cues.Play(Cues.Cue.Start);
        Run(StartCoreAsync(session, foreground));
    }

    private void FollowUp(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Start(false, typed: true, silent: current?.Silent == true);
        Run(SubmitAsync(text, false));
    }

    private void StartVoiceFromPanel()
    {
        if (current?.Silent == true) return;
        if (current is { Status: var status } && status.StartsWith("回复完成", StringComparison.Ordinal)) { Start(false); return; }
        if (current is not { Ready: true, Sending: false, Editing: false } session) return;
        if (session.State is null) { Start(false); return; }
        if (session.Voice.IsCancellationRequested || session.VoicePending || !settings.MicrophoneEnabled) return;
        Run(ResumeScreenshotVoiceAsync(session));
    }

    private async Task ResumeScreenshotVoiceAsync(Session session)
    {
        session.VoicePending = true;
        session.VoiceFailed = false;
        session.Status = "正在聆听";
        overlay.SetActive(session.Screen, Mood.Listening);
        Refresh(session);
        string spoken = await VoiceAsync(session, useScreenshotTiming: false);
        await CompleteInputAsync(session, spoken, submitScreenshotWhenSilent: false);
    }

    private async Task StartCoreAsync(Session session, nint foreground)
    {
        var token = session.Life.Token;
        var voice = session.VoicePending ? VoiceAsync(session) : Task.FromResult("");
        if (session.State is { } state)
        {
            state.Transition(ScreenshotPhase.Triggered);
            state.Transition(ScreenshotPhase.Capturing);
            await captureLock.WaitAsync(token);
            try
            {
                if (Desktop.GetWindowThreadProcessId(foreground, out uint pid) != 0 && pid == Environment.ProcessId && settings.Target == CaptureTarget.ActiveWindow)
                    throw new UserError("当前活动窗口是 Windows Intelligence。请切换到要分析的窗口。");
                overlay.Suppress(true);
                caption.Hide();
                using var excluded = new AppWindowCaptureExclusion();
                await Task.Run(Desktop.DwmFlush, token);
                session.Image = await ScreenCapture.CaptureAsync(settings.Target, session.Screen, foreground, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                session.Voice.Cancel();
                session.CaptureFailed = true;
                state.Transition(ScreenshotPhase.Failed);
                Fail(session, ex.Message);
                return;
            }
            finally { overlay.Suppress(false); captureLock.Release(); }
            if (!IsCurrent(session)) return;
            state.Transition(ScreenshotPhase.Captured);
            state.Transition(ScreenshotPhase.Preview);
            overlay.SetActive(session.Screen, Mood.Screenshot);
            overlay.Pulse();
            session.Status = "截图已捕获 · 等待语音";
            caption.SetImage(session.Image, settings.Preview || !AutoApproved(session), !settings.ReduceMotion);
            Refresh(session);
            if (settings.Target == CaptureTarget.SelectRegion)
            {
                await EditAsync(true);
                if (!IsCurrent(session)) return;
            }
            state.Transition(ScreenshotPhase.WaitingForSpeech);
            if (session.SpeechStarted) state.Transition(ScreenshotPhase.Listening);
        }
        await CompleteInputAsync(session, await voice, submitScreenshotWhenSilent: true);
    }

    private async Task CompleteInputAsync(Session session, string spoken, bool submitScreenshotWhenSilent)
    {
        if (!IsCurrent(session) || session.Sending) return;
        session.VoicePending = false;
        session.VoiceDraft = "";
        if (session.VoiceFailed) { Fail(session, session.Status); return; }
        string final = spoken.Trim();
        if (final.Length > 0) session.Input = final;
        if (session.State is { Phase: ScreenshotPhase.Listening } phase) phase.Transition(ScreenshotPhase.Transcribing);
        session.State?.Transition(ScreenshotPhase.ReadyToSend);
        session.Ready = true;
        session.Status = session.Image is not null ? "截图已就绪" : final.Length > 0 ? "语音已识别" : "输入问题";
        session.AutomaticSubmissionPending = session.Image is not null
            ? AutoApproved(session) && (final.Length > 0 || submitScreenshotWhenSilent)
            : session.CanAutoSend && final.Length > 0;
        if (session.AutomaticSubmissionPending && !session.Editing)
            await SendPendingInputAsync(session);
        else
            Refresh(session);
    }

    private async Task SendPendingInputAsync(Session session)
    {
        if (!IsCurrent(session) || !session.AutomaticSubmissionPending || session.Editing) return;
        session.AutomaticSubmissionPending = false;
        if (session.Image is null || AutoApproved(session)) await SubmitAsync("", true);
        else Refresh(session);
    }

    private async Task<string> VoiceAsync(Session session, bool useScreenshotTiming = true)
    {
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(session.Life.Token, session.Voice.Token);
        void Preview(string text) => Post(session, () =>
        {
            if (!session.VoicePending || session.Sending) return;
            session.VoiceDraft = text;
            Refresh(session);
        });
        try
        {
            await micLock.WaitAsync(ct.Token);
            try
            {
                if (captureSpeech is not null) return await captureSpeech(Preview, ct.Token);
                var clock = useScreenshotTiming ? session.Clock : Stopwatch.StartNew();
                return await recognition.ListenAsync(settings, useScreenshotTiming && session.State is not null, clock, session.Release,
                    level => { if (IsCurrent(session)) Volatile.Write(ref audioLevel, level); },
                    Preview,
                    active =>
                    {
                        if (!active || session.SpeechStarted) return;
                        session.SpeechStarted = true;
                        Post(session, () =>
                        {
                            if (!session.VoicePending || session.Sending) return;
                            session.State?.Transition(ScreenshotPhase.Listening);
                            if (session.State is null || session.Image is not null)
                            {
                                overlay.SetActive(session.Screen, Mood.Listening);
                                session.Status = "正在聆听"; Refresh(session);
                            }
                        });
                    }, ct.Token, () => Post(session, () =>
                    {
                        if (!session.VoicePending || session.Sending) return;
                        session.Status = "正在完成语音识别";
                        Refresh(session);
                    }));
            }
            finally { micLock.Release(); }
        }
        catch (OperationCanceledException) { return ""; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || !IsCurrent(session) || session.Sending) return "";
            session.VoiceFailed = true;
            session.Status = ex is UserError ? ex.Message : $"麦克风或识别器异常 (0x{ex.HResult:X8})";
            if (session.State is null || session.Image is not null) Refresh(session);
            return "";
        }
    }

    public void Release() => current?.Release.Mark(current.Clock.ElapsedMilliseconds);

    private async Task SubmitAsync(string typed, bool automatic)
    {
        if (current is not { Ready: true, Sending: false, Editing: false } session || session.CaptureFailed) return;
        if (automatic && session.Image is not null && !AutoApproved(session)) return;
        if (!automatic && session.VoicePending && string.IsNullOrWhiteSpace(typed)) return;
        if (!string.IsNullOrWhiteSpace(typed)) session.Input = typed.Trim();
        if (string.IsNullOrWhiteSpace(session.Input) && session.Image is null) return;
        if (session.State is { Phase: ScreenshotPhase.Failed } failed && !failed.Retry()) return;
        if (session.State is { } state && !state.Transition(ScreenshotPhase.Uploading)) return;
        session.Sending = true;
        session.VoicePending = session.AutomaticSubmissionPending = false;
        session.VoiceDraft = "";
        session.Failed = false;
        session.Voice.Cancel();
        session.Status = session.Image is not null ? "正在提交截图与问题" : "正在连接 AI";
        session.Answer = "";
        overlay.SetActive(session.Screen, Mood.Thinking);
        Refresh(session);
        var token = session.Life.Token;
        using var speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool speak = !session.Silent && settings.AutoSpeak && settings.Tts != TtsMode.Disabled;
        var segments = speak ? Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }) : null;
        var sentences = segments is null ? null : new Sentences(segments.Writer);
        var voice = segments is null ? null : new SpeechPipeline(speech, settings);
        var pending = new System.Text.StringBuilder();
        Task playback = Task.CompletedTask;
        bool doneCue = false;
        try
        {
            var image = session.Image is { } bitmap ? await Task.Run(() => ScreenCapture.Encode(bitmap, settings.ImageFormat, settings.JpegQuality, settings.MaxImageSide), token) : null;
            if (!IsCurrent(session)) return;
            var request = MultimodalRequestBuilder.Build(session.Id, settings, session.Input, image, history);
            var key = SecretStore.Get(request.Provider.Id);
            voice?.Start(segments!.Reader, token);
            if (settings.Cue && !session.Silent) Cues.Loop(Cues.Cue.Wait);
            var answer = await ai.ReplyAsync(request, key, delta =>
            {
                sentences?.Add(delta);
                pending.Append(delta);
                // With voice enabled the text is held back until the audio has a head start.
                if (sentences is not null) return;
                Post(session, () =>
                {
                    session.State?.Transition(ScreenshotPhase.AiProcessing);
                    if (session.State?.Phase == ScreenshotPhase.Completed) return;
                    session.Answer += delta;
                    session.Status = "正在接收回复";
                    Refresh(session);
                });
            }, token, settings.PowerShellEnabled && session.State is null
                ? (command, ct) => RunPowerShellAsync(session, command, ct)
                : null);
            sentences?.Flush();
            segments?.Writer.TryComplete();
            if (!IsCurrent(session)) return;
            if (voice is not null)
            {
                session.Status = "正在生成语音";
                Refresh(session);
                await WaitForAudioAsync(voice, token);
                if (!IsCurrent(session)) return;
            }
            session.State?.Transition(ScreenshotPhase.Completed);
            session.Answer = pending.ToString();
            session.Status = voice is null ? "回复完成" : "回复完成 · 正在朗读";
            session.Image = null;
            caption.SetImage(null, false);
            Cues.Stop();
            if (settings.Cue && !session.Silent) Cues.Play(Cues.Cue.Done);
            doneCue = true;
            string historyPrompt = image is null ? request.Text : $"[截图] {request.Text}";
            if (settings.ImageHistory && AutoApproved(session))
            {
                for (int i = 0; i < history.Count; i++) history[i] = history[i] with { Image = null };
            }
            history.Add(new AiTurn(historyPrompt, answer, settings.ImageHistory && AutoApproved(session) ? image : null));
            if (history.Count > 50) history.RemoveAt(0);
            SaveHistory();
            Refresh(session);
            if (voice is not null)
            {
                playback = voice.PlayAsync(level =>
                {
                    if (IsCurrent(session)) Volatile.Write(ref audioLevel, level);
                    if (level > .02f) Post(session, () => overlay.SetActive(session.Screen, Mood.Speaking));
                }, speechCancellation.Token);
                try { await playback; }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (!token.IsCancellationRequested) { session.Status = $"回复完成 · 朗读失败：{Format(ex)}"; }
            }
            if (IsCurrent(session))
            {
                session.Status = voice is { Failed: true } ? "回复完成 · 语音生成失败" : "回复完成";
                overlay.Dismiss();
                Refresh(session);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (IsCurrent(session)) Fail(session, Format(ex)); }
        finally
        {
            speechCancellation.Cancel();
            if (!doneCue) Cues.Stop();
            segments?.Writer.TryComplete();
            voice?.Clear();
            try { await playback; } catch { }
            session.Sending = false;
            session.Ready = session.Failed;
            if (IsCurrent(session)) Refresh(session);
        }
    }

    private async Task<PowerShellResult> RunPowerShellAsync(Session session, string command, CancellationToken token)
    {
        await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(session) || !settings.PowerShellEnabled) throw new UserError("自动操作已关闭。");
            session.Status = "正在操作电脑";
            Refresh(session);
        }, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        var result = await PowerShellRunner.RunAsync(command, token).ConfigureAwait(false);
        Post(session, () =>
        {
            session.Status = "正在检查操作结果";
            Refresh(session);
        });
        return result;
    }

    // Wait until roughly four seconds of speech exists, so the answer and the audio land together.
    private static async Task WaitForAudioAsync(SpeechPipeline voice, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(14);
        while (!voice.Drained && !voice.Failed && voice.BufferedSeconds < 4.5 && DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(60, ct);
        }
    }

    private async Task RetryAsync()
    {
        if (current is { CaptureFailed: true }) { Start(true); return; }
        await SubmitAsync("", false);
    }

    private async Task EditAsync(bool initial)
    {
        if (current is not { Editing: false, Sending: false, Image: { } image } session) return;
        session.Editing = true;
        try
        {
            var result = await ImageEditor.EditAsync(image, session.Screen, initial, session.Life.Token);
            if (!IsCurrent(session)) return;
            if (result is null && initial) { Cancel(); return; }
            if (result is not null) session.Image = result;
            caption.SetImage(session.Image, true);
        }
        finally { session.Editing = false; if (IsCurrent(session)) Refresh(session); }
        await SendPendingInputAsync(session);
    }

    public async Task PreviewTtsAsync()
    {
        Cancel();
        var control = previewSpeech = new();
        try { await speech.SpeakAsync("你好，我是 Windows Intelligence。This is a voice preview.", settings, _ => { }, control.Token); }
        finally { if (ReferenceEquals(previewSpeech, control)) previewSpeech = null; control.Dispose(); }
    }

    public async Task<string> PreviewRecognitionAsync(Action<float> level, Action<string> preview, Action finalizing,
        CancellationToken token)
    {
        await micLock.WaitAsync(token);
        try
        {
            return await recognition.ListenAsync(settings, false, Stopwatch.StartNew(), new ReleaseSignal(),
                level, preview, _ => { }, token, finalizing);
        }
        finally { micLock.Release(); }
    }

    public void Cancel(bool animate = false)
    {
        previewSpeech?.Cancel();
        var previous = current;
        current = null;
        previous?.State?.Transition(ScreenshotPhase.Cancelled);
        previous?.Life.Cancel();
        Cues.Stop();
        Volatile.Write(ref audioLevel, 0);
        caption.Hide(animate); caption.SetImage(null, false);
        overlay.Dismiss();
    }

    private bool AutoApproved(Session session)
    {
        try { return session.CanAutoSend && settings.AutoSend && settings.ScreenshotConsent == settings.DestinationKey; }
        catch (UserError) { return false; }
    }

    private void Refresh(Session session)
    {
        if (!IsCurrent(session) || (session.State?.Phase is ScreenshotPhase.Triggered or ScreenshotPhase.Capturing)) return;
        string warning = "";
        if (session.Image is not null && !session.Sending)
        {
            try { warning = $"发送会将此截图传至 {AiEndpoint.Create(settings.Provider.BaseUrl).Authority}。"; }
            catch (UserError ex) { warning = ex.Message; }
        }
        if (session.Image is null && settings.ImageHistory && history.Any(t => t.Image is not null))
            warning = "已启用图片上下文，下一次发送将附带上一张截图。可在隐私设置中清除。";
        string displayedInput = session.VoicePending && session.VoiceDraft.Length > 0 ? session.VoiceDraft : session.Input;
        caption.State(session.Status, displayedInput, session.Answer, session.Ready && !session.Sending, session.Failed && !session.Sending,
            warning, session.VoicePending);
        caption.Show();
        var (_, _, scale) = overlay.Position(session.Screen);
        caption.Place(session.Screen, scale);
    }

    private void Fail(Session session, string message)
    {
        session.State?.Transition(ScreenshotPhase.Failed);
        session.Failed = true;
        session.Ready = !session.CaptureFailed;
        session.Status = message;
        overlay.SetActive(session.Screen, Mood.Error);
        overlay.Settle(TimeSpan.FromSeconds(1.8));
        Refresh(session);
    }

    private void Post(Session session, Action action) => dispatcher.InvokeAsync(() => { if (IsCurrent(session)) action(); }, DispatcherPriority.Normal);
    private bool IsCurrent(Session session) => !disposed && ReferenceEquals(Volatile.Read(ref current), session);
    private void Run(Task task) => Ui.Observe(task, ex => { if (current is { } session) Fail(session, Format(ex)); });
    private static string Format(Exception ex) => ex is UserError ? ex.Message : $"操作失败 (0x{ex.HResult:X8})。";

    private List<AiTurn> LoadHistory()
    {
        if (!settings.SaveTextHistory || !File.Exists(HistoryFile)) return [];
        try { return JsonSerializer.Deserialize<List<AiTurn>>(File.ReadAllText(HistoryFile)) ?? []; }
        catch (IOException) { return []; }
        catch (JsonException) { return []; }
    }

    public void ClearImages()
    {
        for (int i = 0; i < history.Count; i++) history[i] = history[i] with { Image = null };
        if (current is { Image: not null }) Cancel();
    }

    public void ClearHistory() { history.Clear(); File.Delete(HistoryFile); }

    private void SaveHistory()
    {
        if (!settings.SaveTextHistory) return;
        try
        {
            Directory.CreateDirectory(Settings.DirectoryPath);
            var temp = HistoryFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(history.Select(x => x with { Image = null })));
            File.Move(temp, HistoryFile, true);
        }
        catch (IOException) { }
    }

    public void Dispose() { disposed = true; Cancel(); }
}
