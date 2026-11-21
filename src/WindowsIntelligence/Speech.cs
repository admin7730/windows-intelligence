using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace WindowsIntelligence;

internal static class Models
{
    internal static string CpuProvider => "cpu:" + Path.Combine(AppContext.BaseDirectory, "SpeechCpu.config");
    public static string Find(string type, string file)
    {
        var local = Path.Combine(Settings.DirectoryPath, "Models", type, file);
        return File.Exists(local) ? local : Path.Combine(AppContext.BaseDirectory, "Models", type, file);
    }
}

internal sealed class ReleaseSignal
{
    private long released = -1;
    public long Milliseconds => Interlocked.Read(ref released);
    public void Mark(long elapsed) => Interlocked.CompareExchange(ref released, elapsed, -1);
}

internal sealed class SpeechRecognizer : IDisposable
{
    private readonly object sync = new();
    private OnlineRecognizer? recognizer;
    private OfflineRecognizer? finalRecognizer;
    private OfflineRecognizer? accurateRecognizer;
    private bool disposed;
    private readonly IdleModelCache heavyModels;

    internal SpeechRecognizer() => heavyModels = new(ReleaseHeavyModels);
    internal bool HasHeavyModels { get { lock (sync) return finalRecognizer is not null || accurateRecognizer is not null; } }

    internal Task WarmHeavyAsync(bool accurate, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = heavyModels.Acquire();
        ct.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!accurate || PrepareAccurate() is null) PrepareFinal();
        }
    }, ct);

    private void ReleaseHeavyModels()
    {
        lock (sync)
        {
            accurateRecognizer?.Dispose(); accurateRecognizer = null;
            finalRecognizer?.Dispose(); finalRecognizer = null;
        }
    }

    internal OfflineRecognizer? PrepareFinal()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (finalRecognizer is not null) return finalRecognizer;
            var model = Models.Find("ASR", "SenseVoice/model.int8.onnx");
            var tokens = Models.Find("ASR", "SenseVoice/tokens.txt");
            if (!File.Exists(model) || !File.Exists(tokens)) return null;
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.SenseVoice.Model = model;
            config.ModelConfig.SenseVoice.Language = "auto";
            config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            config.ModelConfig.Tokens = tokens;
            config.ModelConfig.Provider = Models.CpuProvider;
            config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
            return finalRecognizer = new OfflineRecognizer(config);
        }
    }

    internal static bool HasAccurateModel => AccurateModelDirectory() is not null;

    private static string? AccurateModelDirectory()
    {
        string directory = Path.GetDirectoryName(Models.Find("ASR", "Qwen3/conv_frontend.onnx"))!;
        return new[] { "conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx",
            "tokenizer/vocab.json", "tokenizer/merges.txt", "tokenizer/tokenizer_config.json" }
            .All(file => File.Exists(Path.Combine(directory, file))) ? directory : null;
    }

    private OfflineRecognizer? PrepareAccurate()
    {
        if (accurateRecognizer is not null) return accurateRecognizer;
        if (AccurateModelDirectory() is not { } directory) return null;
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Qwen3Asr.ConvFrontend = Path.Combine(directory, "conv_frontend.onnx");
        config.ModelConfig.Qwen3Asr.Encoder = Path.Combine(directory, "encoder.int8.onnx");
        config.ModelConfig.Qwen3Asr.Decoder = Path.Combine(directory, "decoder.int8.onnx");
        config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(directory, "tokenizer");
        config.ModelConfig.Provider = Models.CpuProvider;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        return accurateRecognizer = new OfflineRecognizer(config);
    }

    internal string FinalizeUtterance(float[] samples, string preview, CancellationToken ct, bool accurate = true)
    {
        using var lease = heavyModels.Acquire();
        ct.ThrowIfCancellationRequested();
        if (samples.Length < 1600) return preview;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!accurate)
            {
                accurateRecognizer?.Dispose();
                accurateRecognizer = null;
            }
            string text = accurate ? Decode(PrepareAccurate()) : "";
            if (text.Length == 0) text = Decode(PrepareFinal());
            return text.Length > 0 ? text : preview;

            string Decode(OfflineRecognizer? model)
            {
                if (model is null) return "";
                using var stream = model.CreateStream();
                stream.AcceptWaveform(16000, samples);
                model.Decode(stream);
                ct.ThrowIfCancellationRequested();
                string result = stream.Result.Text.Trim();
                return result.Any(char.IsLetterOrDigit) ? result : "";
            }
        }
    }

    internal OnlineRecognizer Prepare()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recognizer is not null) return recognizer;
            string Require(string name)
            {
                string path = Models.Find("ASR", name);
                if (!File.Exists(path)) throw new UserError("缺少中英双语语音模型。请检查随安装包提供的模型文件。");
                return path;
            }
            var config = new OnlineRecognizerConfig();
            config.FeatConfig.SampleRate = 16000;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Transducer.Encoder = Require("encoder-epoch-99-avg-1.int8.onnx");
            config.ModelConfig.Transducer.Decoder = Require("decoder-epoch-99-avg-1.int8.onnx");
            config.ModelConfig.Transducer.Joiner = Require("joiner-epoch-99-avg-1.int8.onnx");
            config.ModelConfig.Tokens = Require("tokens.txt");
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
            // Beam search costs a little CPU but is noticeably more accurate than greedy decoding.
            config.DecodingMethod = "modified_beam_search";
            config.MaxActivePaths = 4;
            config.EnableEndpoint = 0;
            return recognizer = new OnlineRecognizer(config);
        }
    }

    public Task<string> ListenAsync(Settings settings, bool screenshot, Stopwatch clock, ReleaseSignal release,
        Action<float> energy, Action<string> transcript, Action<bool> listening, CancellationToken ct, Action? finalizing = null)
        => Task.Run(async () =>
        {
            using var lease = heavyModels.Acquire();
            return await ListenCoreAsync(settings, screenshot, clock, release, energy, transcript, listening, ct, finalizing).ConfigureAwait(false);
        }, ct);

    public Task<string> TranscribeFileAsync(string path, Action<int> progress, CancellationToken ct) => Task.Run(() =>
    {
        using var lease = heavyModels.Acquire();
        using var audio = new AudioFileReader(path);
        ISampleProvider input = audio;
        if (input.WaveFormat.Channels == 2) input = new StereoToMonoSampleProvider(input) { LeftVolume = .5f, RightVolume = .5f };
        if (input.WaveFormat.Channels != 1) throw new UserError("请选择单声道或双声道音频。");
        if (input.WaveFormat.SampleRate != 16000) input = new WdlResamplingSampleProvider(input, 16000);
        var model = !HasAccurateModel && PrepareFinal() is null ? Prepare() : null;
        var text = new List<string>();
        while (true)
        {
            using var stream = model?.CreateStream();
            var recorded = new List<float>(32 * 16000);
            var buffer = new float[1600];
            int count = 0, segment = 0;
            while (segment < 320 && (count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var samples = count == buffer.Length ? buffer : buffer[..count];
                if (model is null) recorded.AddRange(samples);
                else
                {
                    stream!.AcceptWaveform(16000, samples);
                    while (model.IsReady(stream)) model.Decode(stream);
                }
                segment++;
            }
            if (segment == 0) break;
            string line;
            if (model is null) line = FinalizeUtterance(recorded.ToArray(), "", ct);
            else
            {
                stream!.AcceptWaveform(16000, new float[4800]); stream.InputFinished();
                while (model.IsReady(stream)) model.Decode(stream);
                line = model.GetResult(stream).Text.Trim();
            }
            if (line.Length > 0) text.Add(line);
            progress((int)Math.Clamp(audio.CurrentTime.TotalSeconds * 100 / Math.Max(.01, audio.TotalTime.TotalSeconds), 0, 100));
            if (count == 0) break;
        }
        if (text.Count == 0) throw new UserError("这段音频没有识别到文字。");
        return string.Join("\n", text);
    }, ct);

    internal static (string Id, string Name)[] Microphones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(device =>
            {
                using (device) return (device.ID, device.FriendlyName);
            }).ToArray();
        }
        catch { return []; }
    }

    private static IWaveIn Microphone(string id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (id.Length > 0)
            {
                using var selected = enumerator.GetDevice(id);
                if (selected.State == DeviceState.Active) return new WasapiCapture(selected, false, 40);
            }
            using var preferred = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return new WasapiCapture(preferred, false, 40);
        }
        catch (Exception)
        {
            return new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 30, NumberOfBuffers = 4 };
        }
    }

    private async Task<string> ListenCoreAsync(Settings settings, bool screenshot, Stopwatch clock, ReleaseSignal release,
        Action<float> energy, Action<string> transcript, Action<bool> listening, CancellationToken ct, Action? finalizing)
    {
        if (!settings.MicrophoneEnabled) return "";
        var blocks = Channel.CreateBounded<(float[] Buffer, long Time)>(new BoundedChannelOptions(1100) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        using var mic = Microphone(settings.MicrophoneDeviceId);
        var format = mic.WaveFormat is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : mic.WaveFormat;
        var buffered = new BufferedWaveProvider(format) { ReadFully = false };
        ISampleProvider input = buffered.ToSampleProvider();
        if (format.Channels == 2) input = new StereoToMonoSampleProvider(input);
        if (format.Channels > 2) input = new MultiplexingSampleProvider([input], 1);
        if (format.SampleRate != 16000) input = new WdlResamplingSampleProvider(input, 16000);
        mic.DataAvailable += (_, e) =>
        {
            try
            {
                buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
                var samples = new float[(int)Math.Ceiling(e.BytesRecorded / (double)format.BlockAlign * 16000 / format.SampleRate) + 128];
                int count = input.Read(samples, 0, samples.Length);
                if (count > 0 && !blocks.Writer.TryWrite((samples[..count], clock.ElapsedMilliseconds)))
                    blocks.Writer.TryComplete(new UserError("麦克风处理积压，请重试。"));
            }
            catch (Exception ex) { blocks.Writer.TryComplete(ex); }
        };
        mic.RecordingStopped += (_, e) => blocks.Writer.TryComplete(e.Exception);
        try { mic.StartRecording(); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { throw new UserError($"无法打开麦克风 (0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message})。请检查 Windows 麦克风权限和默认输入设备。"); }

        bool recording = true;
        try
        {
            listening(false);
            var model = Prepare();
            // Start loading while the microphone is already recording. Do not wait on the UI thread.
            var warming = WarmHeavyAsync(settings.AccurateSpeech, ct);
            _ = warming.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            using var stream = model.CreateStream();
            using var vad = new VoiceActivity();
            var recorded = new List<float>(16_000 * 8);
            bool announcedSpeech = false;
            string lastText = "";
            long begun = clock.ElapsedMilliseconds;
            while (clock.ElapsedMilliseconds < begun + 60_000)
            {
                ct.ThrowIfCancellationRequested();
                bool hadData = false;
                while (blocks.Reader.TryRead(out var block))
                {
                    hadData = true;
                    if (screenshot && settings.Strict && release.Milliseconds >= 0 && block.Time > release.Milliseconds)
                    {
                        int keep = block.Buffer.Length - (int)Math.Min(block.Buffer.Length, (block.Time - release.Milliseconds) * 16);
                        if (keep == 0) break;
                        block = (block.Buffer[..keep], release.Milliseconds);
                    }
                    var samples = block.Buffer;
                    float amplitude = vad.Observe(samples, block.Time);
                    recorded.AddRange(samples);
                    energy(amplitude);
                    if (vad.Started && !announcedSpeech) { announcedSpeech = true; listening(true); }
                    stream.AcceptWaveform(16000, samples);
                    while (model.IsReady(stream)) model.Decode(stream);
                    string current = model.GetResult(stream).Text.Trim();
                    if (current.Length > 0 && current != lastText)
                    {
                        lastText = current;
                        transcript(current);
                    }
                    ct.ThrowIfCancellationRequested();
                }
                if (blocks.Reader.Completion.IsCompleted) await blocks.Reader.Completion.ConfigureAwait(false);
                if (blocks.Reader.Completion.IsCompletedSuccessfully) break;
                long elapsed = clock.ElapsedMilliseconds;
                long released = release.Milliseconds;
                int wait = screenshot ? Math.Clamp(settings.VoiceWait, 300, 10000) : 6300;
                long deadline = screenshot ? released >= 0 ? Math.Max(wait, released + wait) : wait : begun + wait;
                if ((screenshot && settings.Strict && released >= 0 && elapsed >= released + 80) ||
                    (vad.Started && elapsed - vad.LastSpeech >= Math.Max(1500, settings.Silence) && (!screenshot || released >= 0 || elapsed > 10_000)) ||
                    (!vad.Started && elapsed >= deadline && (!screenshot || released >= 0 || elapsed >= wait + 2400))) break;
                if (!hadData) await Task.Delay(24, ct).ConfigureAwait(false);
            }
            mic.StopRecording();
            recording = false;
            heavyModels.SpeechEnded();
            energy(0);
            if (!vad.Started) return "";
            finalizing?.Invoke();
            stream.AcceptWaveform(16000, new float[4800]);
            stream.InputFinished();
            while (model.IsReady(stream)) model.Decode(stream);
            string final = model.GetResult(stream).Text.Trim();
            return FinalizeUtterance(vad.Utterance(recorded), final.Length == 0 ? lastText : final, ct, settings.AccurateSpeech);
        }
        finally
        {
            if (recording) { mic.StopRecording(); heavyModels.SpeechEnded(); }
            energy(0);
        }
    }

    public void Dispose()
    {
        heavyModels.Dispose();
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            recognizer?.Dispose();
            recognizer = null;
            finalRecognizer?.Dispose();
            finalRecognizer = null;
            accurateRecognizer?.Dispose();
            accurateRecognizer = null;
        }
    }
}

internal sealed class VoiceActivity : IDisposable
{
    private readonly VoiceActivityDetector? detector = CreateDetector();
    private float preceding, smooth, floor = .003f;
    private int speechSamples, observed, firstSpeech, lastSpeech;
    public bool Started { get; private set; }
    public long LastSpeech { get; private set; }

    private static VoiceActivityDetector? CreateDetector()
    {
        string model = Models.Find("ASR", "silero_vad.onnx");
        if (!File.Exists(model)) return null;
        var config = new VadModelConfig();
        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.SileroVad.Model = model;
        config.SileroVad.Threshold = .32f;
        config.SileroVad.WindowSize = 512;
        config.SileroVad.MinSpeechDuration = .10f;
        config.SileroVad.MinSilenceDuration = .20f;
        config.SileroVad.MaxSpeechDuration = 60f;
        return new VoiceActivityDetector(config, 65);
    }

    public float Observe(float[] samples, long elapsed)
    {
        double square = 0;
        foreach (float input in samples)
        {
            smooth = input - preceding + .968f * smooth;
            preceding = input;
            square += smooth * smooth;
        }
        float rms = samples.Length == 0 ? 0 : (float)Math.Sqrt(square / samples.Length);
        observed += samples.Length;
        bool active;
        if (detector is not null)
        {
            detector.AcceptWaveform(samples);
            active = detector.IsSpeechDetected();
            while (!detector.IsEmpty()) detector.Pop();
        }
        else
        {
            if (rms < floor * 2.2f) floor = .96f * floor + .04f * rms;
            bool aboveFloor = rms > Math.Max(.0035f, floor * (Started ? 2.1f : 2.6f));
            speechSamples = aboveFloor ? Math.Min(speechSamples + samples.Length, 3200) : Math.Max(speechSamples - samples.Length, 0);
            active = Started ? aboveFloor : speechSamples >= 1440;
        }
        if (active)
        {
            if (!Started) firstSpeech = observed - samples.Length;
            Started = true;
            lastSpeech = observed;
            LastSpeech = elapsed;
        }
        return Math.Clamp((rms - .001f) * 12, 0, 1);
    }

    public float[] Utterance(List<float> recording)
    {
        if (!Started) return [];
        // Retain quiet consonants around the detector's boundaries.
        int start = Math.Max(0, firstSpeech - 6400);
        int end = Math.Min(recording.Count, lastSpeech + 6400);
        var result = new float[end - start];
        recording.CopyTo(start, result, 0, result.Length);
        return result;
    }

    public void Dispose() => detector?.Dispose();
}
