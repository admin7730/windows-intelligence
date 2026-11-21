using System.Net.Http.Json;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace WindowsIntelligence;

internal sealed class SpeechOutput : IAsyncDisposable
{
    private readonly SemaphoreSlim synthesis = new(1);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private OfflineTts? local;
    private bool disposed;
    private readonly IdleModelCache localModels;
    internal SpeechOutput() => localModels = new(ReleaseLocalModel);
    internal bool HasLocalModel => local is not null;

    private void ReleaseLocalModel()
    {
        // Local() always owns a cache lease; the callback cannot run during Generate().
        local?.Dispose();
        local = null;
    }

    public async Task SpeakAsync(ChannelReader<string> segments, Settings settings, Action<float> energy, CancellationToken ct)
    {
        await foreach (var sentence in segments.ReadAllAsync(ct).ConfigureAwait(false))
            await SpeakAsync(sentence, settings, energy, ct).ConfigureAwait(false);
    }

    public async Task SpeakAsync(string text, Settings settings, Action<float> energy, CancellationToken ct)
    {
        if (settings.Tts == TtsMode.Disabled || string.IsNullOrWhiteSpace(text)) return;
        await synthesis.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var sound = await CreateAsync(text, settings, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await PlayAsync(sound.ToSampleProvider(), energy, ct).ConfigureAwait(false);
        }
        finally { synthesis.Release(); }
    }

    // Synthesise one sentence without playing it, so the caller can decide when audio starts.
    internal async Task<WaveStream?> SynthesizeAsync(string text, Settings settings, CancellationToken ct)
    {
        if (settings.Tts == TtsMode.Disabled || string.IsNullOrWhiteSpace(text)) return null;
        await synthesis.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await CreateAsync(text, settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        finally { synthesis.Release(); }
    }

    private Task<WaveStream> CreateAsync(string text, Settings settings, CancellationToken ct) => settings.Tts switch
    {
        TtsMode.Local => Task.Run(() => Local(text, settings), ct),
        TtsMode.ElevenLabs => ElevenLabsSpeech.CreateAsync(text, settings, ct),
        TtsMode.MiniMax => MiniMaxSpeech.CreateAsync(text, settings, ct),
        _ => Custom(text, settings, ct)
    };

    internal OfflineTts Prepare()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (local is not null) return local;
        string File(string file) => Models.Find("TTS", file);
        if (!System.IO.File.Exists(File("model.onnx"))) throw new UserError("缺少 Kokoro 本地语音模型。");
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = File("model.onnx");
        config.Model.Kokoro.Voices = File("voices.bin");
        config.Model.Kokoro.Tokens = File("tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(Path.GetDirectoryName(File("model.onnx"))!, "espeak-ng-data");
        config.Model.Kokoro.DictDir = Path.Combine(Path.GetDirectoryName(File("model.onnx"))!, "dict");
        config.Model.Kokoro.Lexicon = $"{File("lexicon-zh.txt")},{File("lexicon-us-en.txt")}";
        config.RuleFsts = $"{File("phone-zh.fst")},{File("date-zh.fst")},{File("number-zh.fst")}";
        config.Model.NumThreads = 4;
        config.Model.Provider = Models.CpuProvider;
        config.MaxNumSentences = 1;
        return local = new OfflineTts(config);
    }

    private WaveStream Local(string text, Settings settings)
    {
        using var lease = localModels.Acquire();
        var model = Prepare();
        var audio = model.Generate(text, (float)Math.Clamp(settings.TtsSpeed, .5, 2), Math.Clamp(settings.LocalVoice, 0, model.NumSpeakers - 1));
        try
        {
            if (audio.NumSamples == 0) throw new UserError("语音模型没有生成音频。");
            // Copy the native PCM directly, avoiding a second full managed float[] per sentence.
            var pcm = new byte[checked(audio.NumSamples * sizeof(float))];
            var native = System.Runtime.InteropServices.Marshal.ReadIntPtr(audio.Handle);
            System.Runtime.InteropServices.Marshal.Copy(native, pcm, 0, pcm.Length);
            return new OwnedPcmStream(new MemoryStream(pcm), WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, 1));
        }
        finally { audio.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        // A native generation cannot be interrupted safely. Wait for its owner before releasing it.
        await synthesis.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            localModels.Dispose();
            local?.Dispose();
            local = null;
        }
        finally { synthesis.Release(); }
    }

    private static async Task<WaveStream> Custom(string text, Settings settings, CancellationToken ct)
    {
        var provider = settings.TtsProvider;
        using var request = AiClient.Create(HttpMethod.Post, AiEndpoint.Create(provider.Url), SecretStore.Get(provider.Id));
        var body = new Dictionary<string, object>
        {
            ["model"] = provider.Model, ["voice"] = provider.Voice, ["input"] = text,
            ["response_format"] = provider.Format, ["speed"] = settings.TtsSpeed
        };
        if (provider.SampleRate > 0) body["sample_rate"] = provider.SampleRate;
        request.Content = JsonContent.Create(body);
        using var reply = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!reply.IsSuccessStatusCode) throw new UserError($"TTS 服务返回 HTTP {(int)reply.StatusCode}。");
        var bytes = await reply.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var memory = new MemoryStream(bytes);
        try { return provider.Format == "mp3" ? new Mp3FileReader(memory) : new WaveFileReader(memory); }
        catch { memory.Dispose(); throw; }
    }

    internal static async Task PlayAsync(ISampleProvider samples, Action<float> energy, CancellationToken ct)
    {
        using var output = new WaveOutEvent { DesiredLatency = 105, NumberOfBuffers = 3 };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is { } error) done.TrySetException(error);
            else done.TrySetResult();
        };
        output.Init(new Meter(samples, energy));
        ct.ThrowIfCancellationRequested();
        output.Play();
        try { await done.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally { output.Stop(); energy(0); }
    }

    private sealed class Meter(ISampleProvider input, Action<float> energy) : ISampleProvider
    {
        public WaveFormat WaveFormat => input.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = input.Read(buffer, offset, count);
            double sum = 0;
            for (int i = offset; i < offset + read; i++) sum += buffer[i] * buffer[i];
            energy(read == 0 ? 0 : Math.Clamp((float)Math.Sqrt(sum / read) * 4.6f, 0, 1));
            return read;
        }
    }
}

// Synthesises sentences ahead of playback so text and audio can be released together.
internal sealed class SpeechPipeline
{
    private readonly Queue<(WaveStream Clip, long MemoryBytes)> ready = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly TaskCompletionSource<bool> firstClip = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly Func<string, CancellationToken, Task<WaveStream?>> synthesize;
    private bool cleared;
    private long bufferedMemoryBytes;
    private volatile bool draining, failed;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal const long MaximumBufferedMemoryBytes = 4 * 1024 * 1024;
    internal long BufferedMemoryBytes { get { lock (gate) return bufferedMemoryBytes; } }
    internal int SpilledClips { get; private set; }

    public SpeechPipeline(SpeechOutput output, Settings settings,
        Func<string, CancellationToken, Task<WaveStream?>>? synthesize = null)
    {
        this.synthesize = synthesize ?? ((text, ct) => output.SynthesizeAsync(text, settings, ct));
    }

    public bool Drained => draining;
    public bool Failed => failed;
    internal Task<bool> WaitForFirstClipAsync(CancellationToken ct) => firstClip.Task.WaitAsync(ct);

    public double BufferedSeconds
    {
        get
        {
            lock (gate)
            {
                double seconds = 0;
                foreach (var clip in ready) seconds += clip.Clip.TotalTime.TotalSeconds;
                return seconds;
            }
        }
    }

    public void Start(ChannelReader<string> segments, CancellationToken ct) =>
        Completion = Task.Run(() => SynthesizeAsync(segments, ct), CancellationToken.None);

    private async Task SynthesizeAsync(ChannelReader<string> segments, CancellationToken ct)
    {
        try
        {
            await foreach (var sentence in segments.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                lock (gate) { if (cleared) return; }
                var clip = await synthesize(sentence, ct).ConfigureAwait(false);
                if (clip is null) { failed = true; continue; }
                if (clip.Length == 0) { clip.Dispose(); failed = true; continue; }
                // Keep generating ahead at the existing pace. Spill excess lossless PCM to a
                // delete-on-close file instead of throttling synthesis or retaining the whole reply.
                long memoryBytes = clip.Length;
                bool spill;
                lock (gate) spill = bufferedMemoryBytes + memoryBytes > MaximumBufferedMemoryBytes;
                if (spill)
                {
                    clip = await SpillAsync(clip, ct).ConfigureAwait(false);
                    memoryBytes = 0;
                    SpilledClips++;
                }
                lock (gate)
                {
                    if (cleared || ct.IsCancellationRequested) { clip.Dispose(); return; }
                    ready.Enqueue((clip, memoryBytes));
                    bufferedMemoryBytes += memoryBytes;
                }
                available.Release();
                firstClip.TrySetResult(true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { failed = true; }
        finally { draining = true; firstClip.TrySetResult(false); }
    }

    public async Task PlayAsync(Action<float> energy, CancellationToken ct)
    {
        while (true)
        {
            var clip = TakeNextClip();
            if (clip is null)
            {
                if (draining) break;
                try { await available.WaitAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            using (clip)
            {
                try { await SpeechOutput.PlayAsync(clip.ToSampleProvider(), energy, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception) { failed = true; }
            }
        }
        energy(0);
    }

    public void Clear()
    {
        lock (gate)
        {
            cleared = true;
            firstClip.TrySetResult(false);
            while (ready.Count > 0) ready.Dequeue().Clip.Dispose();
            bufferedMemoryBytes = 0;
        }
    }

    internal WaveStream? TakeNextClip()
    {
        lock (gate)
        {
            if (ready.Count == 0) return null;
            var next = ready.Dequeue();
            bufferedMemoryBytes -= next.MemoryBytes;
            return next.Clip;
        }
    }

    private static async Task<WaveStream> SpillAsync(WaveStream clip, CancellationToken ct)
    {
        using (clip)
        {
            var path = Path.Combine(Path.GetTempPath(), $"WindowsIntelligence-audio-{Guid.NewGuid():N}.pcm");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                await clip.CopyToAsync(file, ct).ConfigureAwait(false);
                file.Position = 0;
                return new OwnedPcmStream(file, clip.WaveFormat);
            }
            catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
        }
    }
}

// NAudio's RawSourceWaveStream does not close the supplied source stream.
internal class OwnedPcmStream : RawSourceWaveStream
{
    private readonly Stream owned;
    internal OwnedPcmStream(Stream source, WaveFormat format) : base(source, format) => owned = source;

    protected override void Dispose(bool disposing)
    {
        try { if (disposing) owned.Dispose(); }
        finally { base.Dispose(disposing); }
    }
}

internal sealed class Sentences(ChannelWriter<string> output)
{
    private readonly System.Text.StringBuilder pending = new();
    public void Add(string chunk)
    {
        foreach (char c in chunk)
        {
            pending.Append(c);
            if ((pending.Length > 3 && "。！？!?\n".Contains(c)) ||
                (pending.Length > 18 && c == '.') || (pending.Length > 180 && "，, ".Contains(c)) || pending.Length >= 380)
                Flush();
        }
    }
    public void Flush()
    {
        string sentence = pending.ToString().Trim();
        pending.Clear();
        if (sentence.Any(char.IsLetterOrDigit)) output.TryWrite(sentence);
    }
}
