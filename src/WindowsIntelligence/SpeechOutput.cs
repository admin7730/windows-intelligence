using System.Net.Http.Json;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace WindowsIntelligence;

internal sealed class SpeechOutput
{
    private readonly SemaphoreSlim synthesis = new(1);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private OfflineTts? local;

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
        try { return await CreateAsync(text, settings, ct).ConfigureAwait(false); }
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
        if (local is not null) return local;
        string File(string file) => Models.Find("TTS", file);
        if (!System.IO.File.Exists(File("model.onnx"))) throw new UserError("缺少 Kokoro 本地语音模型。");
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = File("model.onnx");
        config.Model.Kokoro.Voices = File("voices.bin");
        config.Model.Kokoro.Tokens = File("tokens.txt");
        config.Model.Kokoro.DictDir = Path.Combine(Path.GetDirectoryName(File("model.onnx"))!, "dict");
        config.Model.Kokoro.Lexicon = $"{File("lexicon-zh.txt")},{File("lexicon-us-en.txt")}";
        config.RuleFsts = $"{File("phone-zh.fst")},{File("date-zh.fst")},{File("number-zh.fst")}";
        config.Model.NumThreads = 4;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        return local = new OfflineTts(config);
    }

    private WaveStream Local(string text, Settings settings)
    {
        var model = Prepare();
        var audio = model.Generate(text, (float)Math.Clamp(settings.TtsSpeed, .5, 2), Math.Clamp(settings.LocalVoice, 0, model.NumSpeakers - 1));
        var samples = audio.Samples;
        if (samples.Length == 0) throw new UserError("语音模型没有生成音频。");
        var pcm = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
        return new RawSourceWaveStream(new MemoryStream(pcm), WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, 1));
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
    private readonly SpeechOutput output;
    private readonly Settings settings;
    private readonly Queue<WaveStream> ready = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly object gate = new();
    private volatile bool draining, failed;

    public SpeechPipeline(SpeechOutput output, Settings settings)
    {
        this.output = output; this.settings = settings;
    }

    public bool Drained => draining;
    public bool Failed => failed;

    public double BufferedSeconds
    {
        get
        {
            lock (gate)
            {
                double seconds = 0;
                foreach (var clip in ready) seconds += clip.TotalTime.TotalSeconds;
                return seconds;
            }
        }
    }

    public void Start(ChannelReader<string> segments, CancellationToken ct) =>
        _ = Task.Run(() => SynthesizeAsync(segments, ct), CancellationToken.None);

    private async Task SynthesizeAsync(ChannelReader<string> segments, CancellationToken ct)
    {
        try
        {
            await foreach (var sentence in segments.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var clip = await output.SynthesizeAsync(sentence, settings, ct).ConfigureAwait(false);
                if (clip is null) { failed = true; continue; }
                lock (gate) ready.Enqueue(clip);
                available.Release();
            }
        }
        catch (OperationCanceledException) { }
        finally { draining = true; }
    }

    public async Task PlayAsync(Action<float> energy, CancellationToken ct)
    {
        while (true)
        {
            WaveStream? clip;
            lock (gate)
            {
                clip = ready.Count > 0 ? ready.Dequeue() : null;
            }
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
            while (ready.Count > 0) ready.Dequeue().Dispose();
        }
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
