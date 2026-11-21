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
        public string ActionDescription = "执行 PowerShell 命令";
        public ExecutionJournal? Journal;
        public bool DraftAnswer, HoldResults, FromSpeaker;
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
    private readonly CancellationTokenSource integrationLife = new();
    internal QqDetection QQ { get; } = new();
    internal XiaoAIRelayClient OfficialXiaoAI { get; }
    private readonly Func<Action<string>, CancellationToken, Task<string>>? captureSpeech;
    private readonly Func<string, CancellationToken, Task<NAudio.Wave.WaveStream?>>? synthesizeSpeech;
    private readonly Func<SpeechPipeline, CancellationToken, Task>? playSpeech;
    private readonly SemaphoreSlim captureLock = new(1);
    private readonly SemaphoreSlim micLock = new(1);
    private readonly List<AiTurn> history;
    private CancellationTokenSource? previewSpeech;
    private Session? current;
    private float audioLevel;
    private bool disposed;
    internal XiaoAIInputProvider XiaoAI { get; }

    public Interaction(Settings settings, Dispatcher dispatcher, Overlay overlay, Caption caption, AiClient? ai = null,
        Func<Action<string>, CancellationToken, Task<string>>? captureSpeech = null,
        Func<string, CancellationToken, Task<NAudio.Wave.WaveStream?>>? synthesizeSpeech = null,
        Func<SpeechPipeline, CancellationToken, Task>? playSpeech = null, bool startIntegrations = true)
    {
        this.settings = settings; this.dispatcher = dispatcher; this.overlay = overlay; this.caption = caption;
        this.ai = ai ?? new();
        this.captureSpeech = captureSpeech;
        this.synthesizeSpeech = synthesizeSpeech; this.playSpeech = playSpeech;
        history = LoadHistory();
        var remoteActions = new XiaoAIWindowsActions(settings.XiaoAI,AskXiaoAIAsync);
        XiaoAI = new(settings.XiaoAI,remoteActions.ExecuteAsync);
        OfficialXiaoAI = new(settings, this);
        if (startIntegrations) { OfficialXiaoAI.Start(); ApplyIntegrations(); _ = MonitorQqAsync(); }
        caption.Submit += text => Run(SubmitAsync(text, false));
        caption.Cancel += () => Cancel(animate: true);
        caption.Speak += StartVoiceFromPanel;
        caption.FollowUp += FollowUp;
        caption.Retry += () => Run(RetryAsync());
        caption.OpenImage += () => Run(EditAsync(false));
        overlay.Place += (screen, scale) => { overlay.Energy = Volatile.Read(ref audioLevel); caption.Place(screen, scale); };
        if (captureSpeech is null) _ = Task.Run(() => { try { recognition.Prepare(); } catch (Exception) { } });
    }

    public string HistoryText => string.Join("\n\n", history.Select(t => $"你\n{t.Text}\n\nWindows Intelligence\n{t.Answer}"));
    private static string HistoryFile => Path.Combine(Settings.DirectoryPath, "history.json");

    internal void ApplyIntegrations()
    {
        if (!settings.XiaoAI.OfficialEnabled && current is { FromSpeaker: true, Sending: true }) Cancel();
        try { QQ.ConfigureFiles(settings.QQ); }
        catch (Exception ex) when (ex is IOException or UserError or ArgumentException or UnauthorizedAccessException)
        { QQ.ReportFileError(ex.Message); }
    }
    private async Task MonitorQqAsync()
    {
        try
        {
            while (!integrationLife.IsCancellationRequested)
            {
                if (settings.QQ.NotificationsEnabled) await QQ.ReadNotificationsAsync();
                await Task.Delay(5000, integrationLife.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    internal Task<bool> CanAcceptSpeakerTaskAsync() => dispatcher.InvokeAsync(() => !disposed &&
        (current is null || !current.Sending && !current.VoicePending && current.Status.StartsWith("回复完成", StringComparison.Ordinal))).Task;

    internal async Task<string> ExecuteSpeakerTaskAsync(string text, CancellationToken ct)
    {
        if (text.Length is 0 or > 4096) throw new UserError("音箱任务长度无效。");
        return await dispatcher.InvokeAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (!settings.XiaoAI.OfficialEnabled) throw new UserError("官方小爱连接已停用。");
            if (current is not null && (current.Sending || current.VoicePending || !current.Status.StartsWith("回复完成", StringComparison.Ordinal)))
                throw new UserError("电脑正在进行其他交互，当前任务未执行，请稍后重试。");
            if (text.Contains("QQ", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("消息") || text.Contains("文件")) &&
                System.Text.RegularExpressions.Regex.IsMatch(text,"查看|查询|读取|读一下|检测|最近|收到|有没有|什么|最新") &&
                !new[] { "发送", "删除", "写入", "打开", "转发", "重命名", "整理", "复制", "移动", "修改", "编辑", "保存" }.Any(text.Contains))
            {
                if (!settings.QQ.AllowSpeakerRead) throw new UserError("尚未允许音箱读取 QQ 检测结果。");
                if (text.Contains("文件") && !settings.QQ.FilesEnabled) throw new UserError("QQ 文件检测尚未启用，请在电脑上选择 QQ 下载目录并启用检测。");
                if (!text.Contains("文件") && !settings.QQ.NotificationsEnabled) throw new UserError("QQ 消息通知检测尚未启用，请先在电脑上启用并授权 Windows 通知访问。");
                if (settings.QQ.NotificationsEnabled) await QQ.ReadNotificationsAsync();
                return QQ.Describe(text);
            }
            if (!settings.PowerShellEnabled) throw new UserError("电脑自动操作已关闭，请先在电脑设置中启用。");
            Start(false, silent: true, canAutoSend: false);
            var session = current!;
            session.FromSpeaker = true;
            using var cancellation = ct.Register(() => dispatcher.InvokeAsync(() => { if (IsCurrent(session)) Cancel(); }));
            await SubmitAsync(text, false);
            ct.ThrowIfCancellationRequested(); session.Life.Token.ThrowIfCancellationRequested();
            if (session.Failed) throw new UserError(session.Status);
            if (string.IsNullOrWhiteSpace(session.Answer)) throw new UserError("电脑未返回有效结果。");
            return session.Answer;
        }).Task.Unwrap();
    }

    private async Task<string> AskXiaoAIAsync(string text, CancellationToken ct)
    {
        var request = await dispatcher.InvokeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            return MultimodalRequestBuilder.Build(Guid.NewGuid(),settings,text,null,[]);
        }).Task.ConfigureAwait(false);
        return await ai.ReplyAsync(request,SecretStore.Get(request.Provider.Id),_ => { },ct).ConfigureAwait(false);
    }

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
            overlay.SetActive(screen);
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
        overlay.SetActive(session.Screen);
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
                using var hidden = new AppWindowCaptureSuppression();
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
            overlay.SetActive(session.Screen);
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
                                overlay.SetActive(session.Screen);
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
        session.Journal = null;
        session.DraftAnswer = false;
        overlay.SetActive(session.Screen);
        Refresh(session);
        var token = session.Life.Token;
        using var speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool speak = !session.Silent && settings.AutoSpeak && settings.Tts != TtsMode.Disabled;
        session.HoldResults = speak;
        var segments = speak ? Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }) : null;
        var sentences = segments is null ? null : new Sentences(segments.Writer);
        var voice = segments is null ? null : new SpeechPipeline(speech, settings, synthesizeSpeech);
        var pending = new System.Text.StringBuilder();
        Task playback = Task.CompletedTask;
        bool doneCue = false;
        try
        {
            var image = session.Image is { } bitmap ? await Task.Run(() => ScreenCapture.Encode(bitmap, settings.ImageFormat, settings.JpegQuality, settings.MaxImageSide), token) : null;
            if (!IsCurrent(session)) return;
            var request = MultimodalRequestBuilder.Build(session.Id, settings, session.Input, image, history);
            var key = SecretStore.Get(request.Provider.Id);
            session.Journal = new(key);
            voice?.Start(segments!.Reader, speechCancellation.Token);
            if (settings.Cue && !session.Silent) Cues.Loop(Cues.Cue.Wait);
            var answer = await ai.ReplyAsync(request, key, delta =>
            {
                sentences?.Add(delta);
                pending.Append(delta);
                Post(session, () =>
                {
                    session.State?.Transition(ScreenshotPhase.AiProcessing);
                    if (session.State?.Phase == ScreenshotPhase.Completed) return;
                    if (speak) { session.Status = "正在准备语音"; Refresh(session); return; }
                    if (session.DraftAnswer) { session.Answer = ""; session.DraftAnswer = false; }
                    session.Answer += delta;
                    session.Status = "正在接收回复";
                    Refresh(session);
                });
            }, token, settings.PowerShellEnabled && session.State is null
                ? (command, ct) => RunPowerShellAsync(session, command, ct)
                : null, activity =>
                {
                    if (activity.Command is not null) session.ActionDescription = activity.Description ?? "执行 PowerShell 命令";
                    Post(session, () =>
                    {
                        session.Status = activity.Status;
                        if (activity.Command is not null) { session.Answer = ""; session.DraftAnswer = false; }
                        else if (!speak && activity.Preview is not null) { session.Answer = activity.Preview; session.DraftAnswer = true; }
                        Refresh(session);
                    });
                });
            sentences?.Flush();
            segments?.Writer.TryComplete();
            if (!IsCurrent(session)) return;
            if (voice is not null)
            {
                session.Status = "正在生成首段语音 · 就绪后显示结果";
                Refresh(session);
                // Release the result on the first playable clip. Later sentences
                // keep synthesizing while the existing playback loop consumes them.
                await voice.WaitForFirstClipAsync(token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(session)) return;
            }
            session.HoldResults = false;
            session.State?.Transition(ScreenshotPhase.Completed);
            session.Answer = pending.ToString();
            session.Status = voice is null ? "回复完成" : voice.Failed ? "回复完成 · 语音生成失败，已显示文字结果" : "回复完成 · 正在朗读";
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
                playback = playSpeech is not null ? playSpeech(voice, speechCancellation.Token) : voice.PlayAsync(level =>
                {
                    if (IsCurrent(session)) Volatile.Write(ref audioLevel, level);
                    if (level > .02f) Post(session, () => overlay.SetActive(session.Screen));
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
        catch (Exception ex)
        {
            if (IsCurrent(session)) { session.HoldResults = false; Fail(session, Format(ex)); }
        }
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
        int step = 0;
        DispatcherTimer? updates = null;
        await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent(session) || !settings.PowerShellEnabled) throw new UserError("自动操作已关闭。");
            session.Journal ??= new();
            step = session.Journal.Begin(command, session.ActionDescription);
            updates = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background,
                (_, _) => { if (IsCurrent(session)) Refresh(session); }, dispatcher);
            session.Status = "正在操作电脑";
            Refresh(session);
        }, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        try
        {
            var result = await PowerShellRunner.RunAsync(command, token, output: (error, text) =>
                session.Journal!.Append(step, error, text)).ConfigureAwait(false);
            session.Journal!.Complete(step, result);
            Post(session, () => { session.Status = "正在检查操作结果"; Refresh(session); });
            return result;
        }
        catch (OperationCanceledException)
        {
            session.Journal!.Complete(step, null, "已取消");
            throw;
        }
        catch (Exception ex)
        {
            session.Journal!.Complete(step, new(null, "", Format(ex)));
            throw;
        }
        finally
        {
            await dispatcher.InvokeAsync(() => updates?.Stop()).Task.ConfigureAwait(false);
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
            warning, session.VoicePending, session.Journal?.Snapshot(revealResults: !session.HoldResults));
        caption.Show();
        caption.Place(session.Screen, overlay.ScaleFor(session.Screen));
    }

    private void Fail(Session session, string message)
    {
        session.State?.Transition(ScreenshotPhase.Failed);
        session.Failed = true;
        session.Ready = !session.CaptureFailed;
        session.Status = message;
        overlay.SetActive(session.Screen);
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

    internal Task ResourceDisposal { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        integrationLife.Cancel(); OfficialXiaoAI.Dispose(); QQ.Dispose();
        Cancel();
        ResourceDisposal = Task.Run(ReleaseSpeechResourcesAsync);
    }

    private async Task ReleaseSpeechResourcesAsync()
    {
        // A cancelled recording still owns its native stream until ListenAsync has returned.
        await micLock.WaitAsync().ConfigureAwait(false);
        try { recognition.Dispose(); }
        finally { micLock.Release(); }
        await speech.DisposeAsync().ConfigureAwait(false);
    }
}
