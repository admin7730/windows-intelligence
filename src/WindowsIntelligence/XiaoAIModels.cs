using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsIntelligence;

internal enum AssistantInputKind { LocalMicrophone, CopilotShortcut, Screenshot, XiaoAISpeaker }
internal sealed record AssistantInput(AssistantInputKind Source, string Text, DateTimeOffset Timestamp,
    string DeviceId = "", string DeviceModel = "", string EventId = "");
internal sealed record XiaoAIDevice(string Id, string Name, string Model, string Firmware = "接口未提供")
{
    public override string ToString() => $"{Name} · {Model}";
    internal static bool IsSupported(string model) => model.Equals("OH2P", StringComparison.OrdinalIgnoreCase) ||
        model.Equals("xiaomi.wifispeaker.oh2p", StringComparison.OrdinalIgnoreCase);
}
internal sealed class XiaoAISettings
{
    public bool OfficialEnabled { get; set; }
    public string RelayUrl { get; set; } = "";
    public bool Enabled { get; set; }
    public bool ComputerControl { get; set; }
    public bool NaturalLanguage { get; set; } = true;
    public bool SpeakerReply { get; set; }
    public bool LocalSpeechFallback { get; set; } = true;
    public string DeviceId { get; set; } = "";
    public string[] TriggerPhrases { get; set; } = ["电脑助手", "电脑上的", "电脑上", "电脑"];
    public Dictionary<string, string> Applications { get; set; } = DefaultApplications();
    public Dictionary<string, string> Websites { get; set; } = new() { ["GitHub"] = "https://github.com" };
    public List<XiaoAIApprovedTask> Tasks { get; set; } = [];
    public string SshHost { get; set; } = "";
    public string SshHostFingerprint { get; set; } = "";
    public string DeviceFirmware { get; set; } = "";

    private static Dictionary<string, string> DefaultApplications()
    {
        var result = new Dictionary<string, string> { ["记事本"] = Path.Combine(Environment.SystemDirectory, "notepad.exe") };
        foreach (string path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe") })
            if (File.Exists(path)) { result["VS Code"] = path; break; }
        return result;
    }
}
internal sealed class XiaoAIApprovedTask
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
internal sealed class XiaoAIEventGate
{
    private readonly object gate = new();
    private readonly DateTimeOffset started;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Id,DateTimeOffset Expires)> order = new();
    private readonly Dictionary<string, long> watermarks;
    internal XiaoAIEventGate(DateTimeOffset started, Dictionary<string, long>? watermarks = null)
    { this.started = started; this.watermarks = watermarks ?? new(); }
    internal IReadOnlyDictionary<string, long> Watermarks { get { lock (gate) return new Dictionary<string,long>(watermarks); } }
    internal bool Accept(AssistantInput input, string selectedDevice, DateTimeOffset now)
    {
        lock (gate)
        {
        if (input.Source != AssistantInputKind.XiaoAISpeaker || input.DeviceId.Length is 0 or >256 || input.DeviceId != selectedDevice || !XiaoAIDevice.IsSupported(input.DeviceModel) ||
            input.Text.Length is 0 or > 4096 || input.EventId.Length is 0 or > 256 || input.Timestamp <= started ||
            now - input.Timestamp > TimeSpan.FromSeconds(30) || input.Timestamp - now > TimeSpan.FromSeconds(5)) return false;
        string device = Hash(input.DeviceId);
        long stamp = input.Timestamp.ToUnixTimeMilliseconds();
        if (watermarks.TryGetValue(device, out long previous) && stamp < previous) return false;
        string id = Hash(input.DeviceId + "|" + input.EventId);
        while (order.TryPeek(out var oldest) && oldest.Expires <= now)
            seen.Remove(order.Dequeue().Id);
        // Fail closed under a burst; never evict a still-valid replay marker to admit new work.
        if (seen.Count >= 1024) return false;
        if (!seen.Add(id)) return false;
        order.Enqueue((id,now.AddSeconds(35)));
        watermarks[device] = Math.Max(stamp, previous);
        return true;
        }
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

internal sealed class XiaoAIDiagnostics
{
    private readonly object gate = new();
    private readonly Queue<object> events = new();
    internal void Add(string category, string code, string eventId = "")
    {
        lock (gate)
        {
            events.Enqueue(new { at = DateTimeOffset.UtcNow, category, code, eventId = eventId.Length == 0 ? "" : XiaoAIEventGate.Hash(eventId) });
            while (events.Count > 100) events.Dequeue();
        }
    }
    internal string Export() { lock (gate) return JsonSerializer.Serialize(events.ToArray(), new JsonSerializerOptions { WriteIndented = true }); }
}
