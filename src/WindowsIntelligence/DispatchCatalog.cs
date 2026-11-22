using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsIntelligence;

internal sealed record DispatchIntent(DispatchTarget Target, string Task)
{
    private const string TargetName = @"(?<target>deep\s*(?:seek|sick)(?:\s*harness)?|harness|深度求索|迪普西克|chat\s*gpt|gpt|codex|科德克斯)";
    private static readonly Regex Chinese = new(
        @"^(?:(?:请你|麻烦你|可以|能不能|能否|帮我|替我|你先|你|请|先|去)\s*)*(?:(?:给|跟|和|向|对)\s*" + TargetName +
        @"\s*(?:说一声|说一下|说|发(?:送|一下|个任务|个消息|一条消息)?|布置(?:个任务)?)|(?:让|叫|找|告诉|派给|发给|转给|交给|转发给|委托给)\s*" + TargetName +
        @"(?![a-z0-9]))\s*[，,：:、。 ]*\s*(?:(?:让|叫|请)(?:他|它|她)\s*)?(?:(?:帮我|替我|来|去)\s*)?(?<task>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TaskFirst = new(
        @"^(?:(?:请你|麻烦你|帮我|替我|你|请)\s*)*把\s*(?<task>.+?)(?:这个任务|这项任务)?\s*(?:转发给|发给|交给|派给|委托给|转给)\s*" + TargetName +
        @"\s*(?:处理|完成|做)?[。！!. ]*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex English = new(
        @"^(?:(?:could you|can you|would you|please|go ahead and)\s+)*(?:(?:ask|tell|get|have)\s+" + TargetName +
        @"\s+to\s+|(?:delegate|send|assign)(?:\s+(?:this|a task|the task|a message))?\s+to\s+" + TargetName +
        @"\s*[:,-]\s*)(?<task>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static DispatchIntent? Parse(string utterance)
    {
        if (utterance.Length is 0 or > 20000) return null;
        string text = utterance.Trim();
        // Normalize only the routing words. Keep the user's task in its original script.
        string normalized = L10n.MapChinese(text, false);
        var match = Chinese.Match(normalized);
        if (!match.Success) match = TaskFirst.Match(normalized);
        if (!match.Success) match = English.Match(normalized);
        if (!match.Success) return null;
        var body = match.Groups["task"];
        string task = (text.Length == normalized.Length ? text.Substring(body.Index, body.Length) : body.Value).Trim();
        if (task.Trim('。', '.', '！', '!', '?', '？', ' ').Length == 0) return null;
        string target = match.Groups["target"].Value.ToLowerInvariant();
        return new(target.Contains("gpt") || target.Contains("codex") || target == "科德克斯"
            ? DispatchTarget.ChatGptCodex : DispatchTarget.DeepSeekHarness, task);
    }
}

internal sealed record NativeDispatchThread(DispatchTarget Target, string Id, string Directory, string Title, DateTimeOffset Updated);
internal sealed record DispatchCatalog(IReadOnlyList<DispatchWorkspace> Projects, IReadOnlyList<NativeDispatchThread> Threads)
{
    private static readonly string[] CodexSources = ["cli", "vscode", "exec", "appServer", "unknown"];

    internal static async Task<DispatchCatalog> LoadAsync(DispatchTarget target, Settings settings, CancellationToken cancellation)
    {
        return target switch
        {
            DispatchTarget.DeepSeekHarness => await Task.Run(() => LoadHarness(cancellation), cancellation).ConfigureAwait(false),
            DispatchTarget.ChatGptCodex => await LoadCodex(settings.Dispatch, cancellation).ConfigureAwait(false),
            _ => new([], [])
        };
    }

    private static DispatchCatalog LoadHarness(CancellationToken cancellation)
    {
        string directory = Environment.GetEnvironmentVariable("DSH_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        string storage = Path.Combine(directory, "storages");
        var projects = new List<DispatchWorkspace>();
        var membership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string workspaceFile = Path.Combine(storage, "workspace.json");
        if (File.Exists(workspaceFile))
        {
            using var document = OpenIndex(workspaceFile);
            var table = Field(Field(document.RootElement, "tables"), "workspaces");
            if (table.ValueKind == JsonValueKind.Object)
                foreach (var entry in table.EnumerateObject())
                {
                    string cwd = LocalDirectory(Text(entry.Value, "path"));
                    if (cwd.Length == 0) continue;
                    string name = Text(entry.Value, "title");
                    projects.Add(new() { Name = name.Length > 0 ? name : ProjectName(cwd), Directory = cwd });
                    var ids = Field(entry.Value, "sessionIds");
                    if (ids.ValueKind == JsonValueKind.Array)
                        foreach (var id in ids.EnumerateArray())
                            if (id.ValueKind == JsonValueKind.String) membership[id.GetString()!] = cwd;
                }
        }

        var threads = new List<NativeDispatchThread>();
        string projections = Path.Combine(storage, "session_projcache", "sessions");
        if (!Directory.Exists(projections)) return new(projects, threads);
        var search = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        // Read only Harness's projection metadata, without opening the conversation logs.
        foreach (string file in Directory.EnumerateFiles(projections, "session-*.json", search))
        {
            cancellation.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(file);
            if (!TaskDispatch.ValidSession(DispatchTarget.DeepSeekHarness, id)) continue;
            try
            {
                using var document = OpenIndex(file);
                var record = Field(document.RootElement, "record");
                var identity = Field(record, "identity");
                var rows = Field(record, "rows");
                string cwd = LocalDirectory(Text(identity, "cwd"));
                if (cwd.Length == 0 && membership.TryGetValue(id, out var member)) cwd = member;
                if (cwd.Length == 0) continue;
                var metadata = Field(Field(rows, "sessionListMetadata"), "val");
                if (Field(metadata, "blank").ValueKind == JsonValueKind.True) continue;
                var title = Field(Field(rows, "title"), "val");
                string name = title.ValueKind == JsonValueKind.String ? title.GetString()! : "";
                threads.Add(new(DispatchTarget.DeepSeekHarness, id, cwd, name, File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return new(projects, threads);
    }

    private static JsonDocument OpenIndex(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 8 * 1024 * 1024) throw new IOException();
        return JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 64 });
    }

    private static async Task<DispatchCatalog> LoadCodex(DispatchSettings settings, CancellationToken cancellation)
    {
        string command = DispatchTools.Codex(settings);
        if (command.Length == 0) throw new UserError("未检测到 Codex，请先安装或配置命令路径。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var process = new Process { StartInfo = DispatchTools.Start(command, ["app-server"], home) };
        ct.ThrowIfCancellationRequested();
        if (!process.Start()) throw new UserError("无法读取 Codex 对话。");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        async Task Send(object request)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        async Task<JsonElement> Read(int id)
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var responseId = Field(root, "id");
                if (responseId.ValueKind != JsonValueKind.Number || responseId.GetInt32() != id) continue;
                var value = Field(root, "result");
                if (value.ValueKind != JsonValueKind.Object) throw new UserError("Codex 未返回本机对话列表。");
                return value.Clone();
            }
            throw new UserError("Codex 未返回本机对话列表。");
        }

        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "windows_intelligence", version = "0.2.48" } } }).ConfigureAwait(false);
            await Read(1).ConfigureAwait(false);
            await Send(new { method = "initialized" }).ConfigureAwait(false);
            int id = 2;
            string? cursor = null;
            var threads = new List<NativeDispatchThread>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                await Send(new { id, method = "thread/list", @params = new { cursor, limit = 100, sortKey = "updated_at", modelProviders = Array.Empty<string>(), sourceKinds = CodexSources } }).ConfigureAwait(false);
                var result = await Read(id++).ConfigureAwait(false);
                foreach (var thread in Field(result, "data").EnumerateArray())
                {
                    string threadId = Text(thread, "id");
                    string cwd = LocalDirectory(Text(thread, "cwd"));
                    if (!TaskDispatch.ValidSession(DispatchTarget.ChatGptCodex, threadId) || cwd.Length == 0) continue;
                    string title = Text(thread, "name");
                    if (title.Length == 0) title = Text(thread, "preview");
                    var updated = Field(thread, "updatedAt");
                    var date = updated.ValueKind == JsonValueKind.Number && updated.TryGetInt64(out long timestamp) && timestamp is > 0 and < 253402300799
                        ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : default;
                    threads.Add(new(DispatchTarget.ChatGptCodex, threadId, cwd, title, date));
                }
                cursor = Text(result, "nextCursor");
                if (cursor.Length > 0 && !cursors.Add(cursor)) throw new UserError("Codex 未返回本机对话列表。");
            } while (cursor.Length > 0);
            return new([], threads);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new UserError("读取 Codex 对话超时，请重试。");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await stderr.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        }
    }

    internal static string ProjectName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
    private static string LocalDirectory(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return ""; }
    }
    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static string Text(JsonElement value, string name) =>
        Field(value, name) is { ValueKind: JsonValueKind.String } field ? field.GetString() ?? "" : "";
}
