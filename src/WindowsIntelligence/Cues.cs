using NAudio;
using NAudio.Wave;

namespace WindowsIntelligence;

// Interface cues: start.wav when the assistant opens, wait.wav looped while the reply is pending,
// done.wav when the answer is ready. Files live in Sounds\ next to the executable.
internal static class Cues
{
    internal enum Cue { Start, Wait, Done }

    private static readonly object Gate = new();
    private static WaveOutEvent? device;
    private static WaveFileReader? reader;
    private static bool looping;
    private static string path = "";

    public static void Play(Cue cue) => Start(cue, false);
    public static void Loop(Cue cue) => Start(cue, true);

    public static void Stop()
    {
        lock (Gate)
        {
            looping = false;
            device?.Stop();
            device?.Dispose();
            device = null;
            reader?.Dispose();
            reader = null;
        }
    }

    private static void Start(Cue cue, bool repeat)
    {
        string name = cue switch { Cue.Start => "start.wav", Cue.Wait => "wait.wav", _ => "done.wav" };
        string file = Path.Combine(AppContext.BaseDirectory, "Sounds", name);
        if (!File.Exists(file)) return;
        try
        {
            lock (Gate)
            {
                looping = false;
                device?.Stop();
                device?.Dispose();
                device = null;
                reader?.Dispose();
                reader = null;
                path = file;
                looping = repeat;
                reader = new WaveFileReader(file);
                device = new WaveOutEvent { DesiredLatency = 120 };
                device.Init(reader);
                device.PlaybackStopped += (_, _) => Restart();
                device.Play();
            }
        }
        catch (Exception ex) when (ex is MmException or InvalidOperationException or PlatformNotSupportedException or IOException) { looping = false; }
    }

    // The waiting cue keeps looping until the answer starts playing or the session is cancelled.
    private static void Restart()
    {
        lock (Gate)
        {
            if (!looping || device is null) { reader?.Dispose(); reader = null; return; }
            try
            {
                reader?.Dispose();
                reader = new WaveFileReader(path);
                device.Init(reader);
                device.Play();
            }
            catch (Exception ex) when (ex is MmException or InvalidOperationException or IOException) { looping = false; }
        }
    }
}
