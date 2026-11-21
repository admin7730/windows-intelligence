using System.Text.Json;
using WindowsIntelligence.Xiaomi;

namespace WindowsIntelligence.SkillRelay;

public sealed record RelayJob(string Id, string Owner, string Query, DateTimeOffset Created, DateTimeOffset Expires,
    string State, string Lease = "", string Reply = "");

// One Windows consumer, bounded durable jobs; a lease is never redelivered.
// Restarted/incomplete work is marked unknown rather than automatically rerun.
public sealed class RelayStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, RelayJob> jobs = [];
    private readonly string directory;
    public RelayStore(string directory)
    {
        this.directory = Path.GetFullPath(directory); Directory.CreateDirectory(this.directory);
        foreach (string file in Directory.EnumerateFiles(this.directory, "*.json").Take(65))
        {
            if (jobs.Count >= 64 || new FileInfo(file).Length > 131072) break;
            try
            {
                var job = JsonSerializer.Deserialize<RelayJob>(File.ReadAllText(file));
                if (job is null || job.Id.Length != 64 || !job.Id.All(Uri.IsHexDigit) || Path.GetFileNameWithoutExtension(file) != job.Id ||
                    job.Owner.Length != 64 || job.Query.Length > 4096 || job.Reply.Length > 4096) continue;
                if (job.State is "queued" or "leased") job = job with { State = "unknown", Reply = "连接已中断，无法确认操作是否完成，请先在电脑核对。" };
                jobs[job.Id] = job; Save(job);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
    }
    private void Save(RelayJob job)
    {
        string path = Path.Combine(directory, job.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(job)); File.Move(path + ".tmp", path, true);
    }
    public RelayJob Submit(SkillRequest request, DateTimeOffset now)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(request.JobId, out var existing)) return existing;
            foreach (var id in jobs.Where(p => p.Value.Expires < now.AddMinutes(-5)).Select(p => p.Key).ToArray())
            { jobs.Remove(id); File.Delete(Path.Combine(directory, id + ".json")); }
            if (jobs.Count >= 64 || jobs.Values.Count(j => j.State is "queued" or "leased") >= 16)
                throw new InvalidOperationException("Queue full");
            var job = new RelayJob(request.JobId, request.Owner, request.Query, now, now.AddMinutes(2), "queued");
            Save(job); jobs.Add(job.Id, job); return job;
        }
    }
    public RelayJob? Find(string owner, string id = "")
    {
        lock (gate) return id.Length > 0 ? jobs.GetValueOrDefault(id) is { } job && job.Owner == owner ? job : null
            : jobs.Values.Where(j => j.Owner == owner).MaxBy(j => j.Created);
    }
    public WindowsTask? Lease(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var expired in jobs.Values.Where(j => j.Expires <= now && j.State is "queued" or "leased").ToArray())
            { var value = expired with { State = "unknown", Reply = "任务已超时，未自动重试，请在电脑核对。" }; Save(value); jobs[value.Id] = value; }
            var job = jobs.Values.Where(j => j.State == "queued").MinBy(j => j.Created);
            if (job is null) return null;
            job = job with { State = "leased", Lease = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) };
            Save(job); jobs[job.Id] = job;
            return new(job.Id, job.Lease, job.Query, job.Expires);
        }
    }
    public bool Complete(WindowsTaskResult result)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(result.JobId, out var job) || result.Lease != job.Lease || result.Lease.Length != 64 ||
                result.State is not ("completed" or "failed" or "cancelled") || result.Reply.Length > 4096) return false;
            if (job.State != "leased") return job.State == result.State && job.Reply == result.Reply;
            var value = job with { State = result.State, Reply = result.Reply };
            Save(value); jobs[job.Id] = value; return true;
        }
    }
}
