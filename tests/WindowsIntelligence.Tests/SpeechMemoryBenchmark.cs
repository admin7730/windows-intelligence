using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsIntelligence;

internal static class SpeechMemoryBenchmark
{
    public static Task RunAsync() => Task.Run(async () =>
    {
        var rows = new List<object>();
        var recognition = new SpeechRecognizer();
        var speech = new SpeechOutput();
        var watch = Stopwatch.StartNew();
        Record("start");
        recognition.Prepare();
        Record("preview-ready");
        for (int round = 0; round < 3; round++)
        {
            foreach (string file in new[] { "zh.wav", "en.wav" })
            {
                watch.Restart();
                string text = await recognition.TranscribeFileAsync(
                    Path.Combine(".tooling", "sensevoice-fixtures", file), _ => { }, CancellationToken.None);
                Record($"asr-{round}-{file}", text);
            }
        }
        for (int round = 0; round < 2; round++)
        {
            watch.Restart();
            using var audio = await speech.SynthesizeAsync("你好，我是 Windows Intelligence。This is a voice preview.",
                new Settings { Tts = TtsMode.Local }, CancellationToken.None);
            if (audio is null || audio.TotalTime.TotalSeconds < .2) throw new InvalidOperationException("Local synthesis failed");
            // Hash generated PCM, excluding container metadata, to detect changes to the voice.
            using var pcm = new MemoryStream();
            audio.CopyTo(pcm);
            Record($"tts-{round}", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pcm.ToArray())));
        }
        recognition.Dispose();
        if (speech is IAsyncDisposable owner) await owner.DisposeAsync();
        Record("disposed");
        string destination = Environment.GetEnvironmentVariable("WI_MEMORY_REPORT")
            ?? Path.Combine(AppContext.BaseDirectory, "speech-memory.json");
        File.WriteAllText(destination, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));

        void Record(string phase, string? output = null)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var row = new { phase, milliseconds = watch.ElapsedMilliseconds,
                workingSetMiB = process.WorkingSet64 / 1048576d,
                privateMiB = process.PrivateMemorySize64 / 1048576d,
                peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576d,
                managedMiB = GC.GetTotalMemory(false) / 1048576d, output };
            rows.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
    });
}
