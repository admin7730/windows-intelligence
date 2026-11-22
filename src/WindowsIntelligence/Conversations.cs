using System.Text.Json;

namespace WindowsIntelligence;

internal sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "新对话";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AiTurn> Turns { get; set; } = [];
}

internal sealed class Conversations
{
    private sealed class Document { public int Version { get; set; } = 1; public string? LegacyHash { get; set; } public List<Conversation> Items { get; set; } = []; }
    private readonly List<Conversation> items = [];
    private readonly string path;
    private readonly Func<bool> persist;
    private readonly Func<DateTimeOffset> clock;
    private Conversation? active;
    private DateTimeOffset lastActivity;
    private string? legacyHash;
    internal string? Warning { get; private set; }
    internal Conversation? Active => active;
    internal IReadOnlyList<Conversation> Items => items.OrderByDescending(c => c.UpdatedAt).ToArray();

    internal Conversations(string directory, Func<bool> persist, Func<DateTimeOffset>? clock = null)
    {
        path = Path.Combine(directory, "conversations.json"); this.persist = persist; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (!persist()) return;
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("History exceeds the read budget.");
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                if (document is null || document.Version != 1 || document.Items is null) throw new JsonException();
                legacyHash = document.LegacyHash;
                foreach (var item in document.Items.TakeLast(300))
                {
                    if (item.Id == Guid.Empty || item.Turns is null || items.Any(c => c.Id == item.Id)) continue;
                    item.Title = string.IsNullOrWhiteSpace(item.Title) ? "以前的对话" : item.Title[..Math.Min(80, item.Title.Length)];
                    item.Turns = item.Turns.Where(t => t is not null && t.Text is not null && t.Answer is not null).TakeLast(100).Select(t => t with { Image = null }).ToList();
                    items.Add(item);
                }
            }
            string legacy = Path.Combine(directory, "history.json");
            if (File.Exists(legacy))
            {
                if (new FileInfo(legacy).Length > 16 * 1024 * 1024) throw new IOException("Legacy history exceeds the read budget.");
                var bytes = File.ReadAllBytes(legacy);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                if (hash != legacyHash)
                {
                    var turns = JsonSerializer.Deserialize<List<AiTurn>>(bytes);
                    if (turns is { Count: > 0 })
                    {
                        var imported = items.FirstOrDefault(c => c.Title == "以前的对话");
                        if (imported is null) { imported = new() { Title = "以前的对话" }; items.Add(imported); }
                        imported.Turns = turns.Where(t=>t is not null && t.Text is not null && t.Answer is not null).TakeLast(100).Select(t=>t with { Image = null }).ToList();
                        imported.UpdatedAt = File.GetLastWriteTimeUtc(legacy);
                    }
                    legacyHash = hash;
                    // Persist the migration with the next real completed turn; loading is read-only.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "历史文件暂不可读，原文件已保留。"; }
        // A fresh app launch never silently resumes a previous private context.
    }

    internal Conversation Begin(int idleMinutes, bool force = false)
    {
        var now = clock();
        if (force || active is null || now < lastActivity || now - lastActivity >= TimeSpan.FromMinutes(Math.Clamp(idleMinutes, 1, 1440)))
        {
            active = new() { CreatedAt = now, UpdatedAt = now };
        }
        lastActivity = now;
        return active;
    }

    internal bool Select(Guid id)
    {
        var found = items.Find(c => c.Id == id);
        if (found is null) return false;
        active = found; lastActivity = clock(); return true;
    }

    internal void Add(Conversation conversation, AiTurn turn)
    {
        if (!items.Contains(conversation))
        {
            if (!ReferenceEquals(active, conversation)) return;
            items.Add(conversation);
        }
        if (conversation.Turns.Count == 0)
        {
            string title = turn.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            conversation.Title = title.Length > 30 ? title[..30] + "…" : title;
        }
        conversation.Turns.Add(turn);
        if (conversation.Turns.Count > 100) conversation.Turns.RemoveAt(0);
        conversation.UpdatedAt = clock();
        if (ReferenceEquals(active, conversation)) lastActivity = conversation.UpdatedAt;
        while (items.Count > 300) items.Remove(items.Where(c => c.Id != conversation.Id).MinBy(c => c.UpdatedAt)!);
        Save();
    }

    internal void ClearImages()
    {
        foreach (var item in items)
            for (int i = 0; i < item.Turns.Count; i++) item.Turns[i] = item.Turns[i] with { Image = null };
    }

    internal void Delete(Guid id)
    {
        items.RemoveAll(c => c.Id == id);
        if (active?.Id == id) active = null;
        Save();
    }

    internal void Clear()
    {
        items.Clear(); active = null;
        foreach (var file in new[] { path, Path.Combine(Path.GetDirectoryName(path)!, "history.json") })
            if (File.Exists(file)) File.Delete(file);
    }

    private void Save()
    {
        if (!persist() || Warning is not null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var clean = items.Where(c => c.Turns.Count > 0).Select(c => new Conversation { Id = c.Id, Title = c.Title,
                CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt, Turns = c.Turns.Select(t => t with { Image = null }).ToList() }).ToList();
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document { Items = clean, LegacyHash = legacyHash }));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "暂时无法保存历史，当前对话仍保留在内存。"; }
    }
}
