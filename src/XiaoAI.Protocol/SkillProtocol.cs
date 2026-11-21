using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsIntelligence.Xiaomi;

public sealed record SkillRequest(string AppId, string UserId, string DeviceId, string SessionId, string RequestId,
    DateTimeOffset Timestamp, int Type, string Query, string Intent, string PreviousJob)
{
    public string Owner => Digest(AppId + "|" + UserId + "|" + DeviceId);
    public string JobId => Digest(Owner + "|" + RequestId);
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static SkillRequest Parse(ReadOnlyMemory<byte> body)
    {
        if (body.Length is 0 or > 65536) throw new FormatException("Invalid request length");
        using var json = JsonDocument.Parse(body, new() { MaxDepth = 16 });
        var root = json.RootElement;
        static string Text(JsonElement node, string key, int maximum = 256, bool required = false)
        {
            string value = node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var item) &&
                item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
            if (value.Length > maximum || required && value.Length == 0 || value.Contains('\0'))
                throw new FormatException("Invalid request field");
            return value;
        }
        static JsonElement Object(JsonElement node, string key) => node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object ? item : default;
        if (Text(root, "version", 8, true) != "1.0") throw new FormatException("Unsupported version");
        var session = Object(root, "session"); var request = Object(root, "request");
        var application = Object(session, "application"); var user = Object(session, "user");
        var context = Object(root, "context"); var intent = Object(request, "intent");
        if (!request.TryGetProperty("type", out var type) || !type.TryGetInt32(out int kind) || kind is < 0 or > 2 ||
            !request.TryGetProperty("timestamp", out var stamp) || !stamp.TryGetInt64(out long milliseconds))
            throw new FormatException("Invalid request type or timestamp");
        string query = Text(root, "query", 4096);
        if (query.Length == 0) query = Text(intent, "query", 4096);
        string intentName = Text(Object(request, "slot_info"), "intent_name");
        if (request.TryGetProperty("no_response", out var absent) && absent.ValueKind == JsonValueKind.True) kind = 2;
        return new(Text(application, "app_id", required: true), Text(user, "user_id"), Text(context, "device_id"),
            Text(session, "session_id", required: true), Text(request, "request_id", required: true),
            DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), kind, query, intentName,
            Text(Object(session, "attributes"), "wi_job_id", 64));
    }
}

public sealed record WindowsTask(string JobId, string Lease, string Query, DateTimeOffset ExpiresAt);
public sealed record WindowsTaskResult(string JobId, string Lease, string State, string Reply);

public static class SkillReplies
{
    public static object Speak(string text, bool end = true, string? jobId = null, bool notUnderstood = false) => new
    {
        version = "1.0", is_session_end = end,
        response = new { open_mic = !end, not_understand = notUnderstood,
            to_speak = new { type = 0, text = text.Length > 2000 ? text[..2000] + "。完整结果请查看电脑。" : text } },
        session_attributes = new { wi_job_id = jobId ?? "" }
    };
    public static bool IsResultQuery(SkillRequest request) => request.Intent == "windows_result" ||
        new[] { "查看结果", "查询结果", "处理结果", "完成了吗", "完成没有" }.Any(request.Query.Contains);
    public static bool IsComputerRequest(SkillRequest request) =>
        request.Intent is "windows_control" or "windows_status" or "qq_files" or "qq_messages" ||
        (new[] { "电脑", "Windows", "QQ", "桌面", "VS Code" }.Any(word => request.Query.Contains(word, StringComparison.OrdinalIgnoreCase)) &&
         new[] { "打开", "启动", "查询", "查看", "检查", "读取", "写入", "创建", "整理", "搜索", "调整", "设置", "运行", "文件", "消息" }.Any(request.Query.Contains));
}

// Official MIAI-HmacSHA256-V1, restricted to the configured POST callback path
// with no query string. Body authentication is mandatory for command execution.
public static class XiaomiSignature
{
    public const string Version = "MIAI-HmacSHA256-V1";
    public static string Canonical(string path, string date, string host, string contentType, string contentMd5) =>
        $"POST\n{path}\n\n{date}\n{host}\n{contentType}\n{contentMd5}\n";
    public static string Sign(byte[] secret, string keyId, string canonical) =>
        $"{Version} {keyId}::{Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(canonical)))}";
    public static bool Verify(byte[] secret, string keyId, string authorization, string path, string date,
        string host, string contentType, string contentMd5, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (secret.Length < 16 || authorization.Length > 512 || date.Length > 80 ||
            !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
            Math.Abs((now - timestamp).TotalSeconds) > 300 || !date.EndsWith("GMT", StringComparison.Ordinal) ||
            new[] { path, date, host, contentType, contentMd5 }.Any(value => value.Contains('\r') || value.Contains('\n')) ||
            !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] md5;
        try { md5 = Convert.FromBase64String(contentMd5); } catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(MD5.HashData(body), md5)) return false;
        string expected = Sign(secret, keyId, Canonical(path, date, host, contentType, contentMd5));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(authorization));
    }
}
