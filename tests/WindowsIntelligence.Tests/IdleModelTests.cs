using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowsIntelligence;

internal static class IdleModelTests
{
    public static Task PolicyAsync()
    {
        var time = DateTimeOffset.UtcNow;
        int released=0;
        using var cache = new IdleModelCache(() => released++,now: () => time);
        var lease = cache.Acquire();
        time = time.AddSeconds(50);
        if (cache.TryExpire()) throw new InvalidOperationException("active native handle was released");
        cache.SpeechEnded();
        lease.Dispose(); lease.Dispose();
        time = time.AddSeconds(29);
        if (cache.TryExpire()) throw new InvalidOperationException("released before 30 seconds after speech");
        var renewed = cache.Acquire(); renewed.Dispose();
        time = time.AddSeconds(2);
        if (cache.TryExpire()) throw new InvalidOperationException("new request failed to reset deadline");
        time = time.AddSeconds(29);
        if (!cache.TryExpire() || released != 1 || cache.TryExpire()) throw new InvalidOperationException("idle cache release not exactly once");
        using (cache.Acquire()) { }
        cache.Dispose();
        time = time.AddSeconds(40);
        if (cache.TryExpire()) throw new InvalidOperationException("disposed timer still released resources");
        try { cache.Acquire(); throw new InvalidOperationException("disposed cache accepted work"); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public static Task NativeAsync() => Task.Run(async () =>
    {
        var rows = new List<object>();
        using var recognition = new SpeechRecognizer();
        await using var speech = new SpeechOutput();
        recognition.Prepare(); Record("necessary-ready");
        if (recognition.HasHeavyModels) throw new InvalidOperationException("startup loaded heavy models");
        await recognition.WarmHeavyAsync(true,CancellationToken.None);
        string first = await recognition.TranscribeFileAsync(Path.Combine(".tooling","sensevoice-fixtures","zh.wav"),_ => { },default);
        using (var audio = await speech.SynthesizeAsync("你好。",new Settings { Tts=TtsMode.Local },default))
            if (audio is null) throw new InvalidOperationException("local synthesis failed");
        if (!recognition.HasHeavyModels || !speech.HasLocalModel) throw new InvalidOperationException("native models not loaded");
        Record("active-models");
        await Task.Delay(TimeSpan.FromSeconds(32));
        if (recognition.HasHeavyModels || speech.HasLocalModel) throw new InvalidOperationException("native idle models retained after 30 seconds");
        Record("idle-released");
        string second = await recognition.TranscribeFileAsync(Path.Combine(".tooling","sensevoice-fixtures","zh.wav"),_ => { },default);
        if (first != second || first.Length == 0) throw new InvalidOperationException("reload changed recognition output");
        Record("reload-verified");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"idle-memory.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions { WriteIndented=true }));
        void Record(string phase)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var row = new { phase,workingSetMiB=process.WorkingSet64/1048576d,privateMiB=process.PrivateMemorySize64/1048576d,
                heavyAsr=recognition.HasHeavyModels,localTts=speech.HasLocalModel };
            rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
        }
    });
}
