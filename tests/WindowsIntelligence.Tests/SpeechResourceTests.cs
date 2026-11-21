using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using WindowsIntelligence;

internal static class SpeechResourceTests
{
    public static async Task RunAsync()
    {
        var existingFiles = new HashSet<string>(Directory.GetFiles(Path.GetTempPath(), "WindowsIntelligence-audio-*.pcm"));
        await using var output = new SpeechOutput();
        var settings = new Settings();
        var pending = new TaskCompletionSource<WaveStream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new SpeechPipeline(output, settings, (_, _) => { entered.SetResult(); return pending.Task; });
        var text = Channel.CreateUnbounded<string>();
        text.Writer.TryWrite("late audio");
        text.Writer.Complete();
        late.Start(text.Reader, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        late.Clear();
        var clip = new TrackedClip();
        pending.SetResult(clip); // Simulate a native/provider operation finishing after ESC.
        await late.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        if (!clip.Released || late.BufferedSeconds != 0)
            throw new InvalidOperationException("Audio completed after cancellation was retained");

        var generated = new List<TrackedClip>();
        var bounded = new SpeechPipeline(output, settings, (_, _) =>
        {
            var next = new TrackedClip();
            generated.Add(next);
            return Task.FromResult<WaveStream?>(next);
        });
        var longAnswer = Channel.CreateUnbounded<string>();
        for (int i = 0; i < 12; i++) longAnswer.Writer.TryWrite("eight seconds");
        longAnswer.Writer.Complete();
        bounded.Start(longAnswer.Reader, CancellationToken.None);
        await bounded.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        if (bounded.BufferedSeconds != 96 || generated.Count != 12 || bounded.SpilledClips == 0 ||
            bounded.BufferedMemoryBytes > SpeechPipeline.MaximumBufferedMemoryBytes)
            throw new InvalidOperationException("Audio generation was throttled or reply memory was unbounded");
        int played = 0;
        while (bounded.TakeNextClip() is { } next)
        {
            using (next)
            {
                if (next.TotalTime.TotalSeconds != 8) throw new InvalidOperationException("Audio duration changed");
                var bytes = new byte[checked((int)next.Length)];
                next.ReadExactly(bytes);
                if (!bytes.AsSpan().SequenceEqual(TrackedClip.Pcm)) throw new InvalidOperationException("PCM bytes changed");
            }
            played++;
        }
        if (played != 12) throw new InvalidOperationException("Buffered reply lost audio clips");
        bounded.Clear();
        if (generated.Exists(value => !value.Released) || bounded.BufferedSeconds != 0)
            throw new InvalidOperationException("Buffered audio leaked on cancellation");
        if (Array.Exists(Directory.GetFiles(Path.GetTempPath(), "WindowsIntelligence-audio-*.pcm"), path => !existingFiles.Contains(path)))
            throw new InvalidOperationException("Spilled audio was not deleted after playback");

        using var recognition = new SpeechRecognizer();
        recognition.Dispose();
        try { recognition.Prepare(); throw new InvalidOperationException("Disposed recognition accepted work"); }
        catch (ObjectDisposedException) { }
        await output.DisposeAsync();
        try { output.Prepare(); throw new InvalidOperationException("Disposed speech accepted work"); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Late audio released; 96s of identical PCM buffered within 4 MiB; native owners dispose safely.");
    }

    private sealed class TrackedClip() : OwnedPcmStream(
        new MemoryStream((byte[])Pcm.Clone()), WaveFormat.CreateIeeeFloatWaveFormat(24000, 1))
    {
        internal static readonly byte[] Pcm = CreatePcm();
        internal bool Released { get; private set; }
        protected override void Dispose(bool disposing) { Released = true; base.Dispose(disposing); }

        private static byte[] CreatePcm()
        {
            var samples = new float[8 * 24000];
            for (int i = 0; i < samples.Length; i++) samples[i] = ((i % 256) - 128) / 640f;
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}
