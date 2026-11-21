using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WindowsIntelligence.Xiaomi;

namespace WindowsIntelligence;

internal sealed class XiaoAIRelayClient : IDisposable
{
    internal static readonly Guid CredentialId = new("9d0bd231-09a0-48fb-a560-35b22044d7cb");
    private readonly Settings settings;
    private readonly Interaction interaction;
    private readonly CancellationTokenSource life = new();
    private readonly HttpClient http;
    private readonly Func<string> tokenSource;
    private Task worker = Task.CompletedTask;
    private string status = "未启用官方技能连接";
    internal string Status => Volatile.Read(ref status);
    internal XiaoAIRelayClient(Settings settings, Interaction interaction, HttpMessageHandler? transport = null, Func<string>? tokenSource = null)
    {
        this.settings = settings; this.interaction = interaction;
        http = new(transport ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
        this.tokenSource = tokenSource ?? (() => SecretStore.Get(CredentialId));
    }
    internal static Uri BaseAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new UserError("技能服务地址需要是 HTTPS 根地址，不含账号、路径参数或片段。");
        return uri;
    }
    internal void Start() => worker = Task.Run(RunAsync);
    private async Task RunAsync()
    {
        while (!life.IsCancellationRequested)
        {
            try
            {
                if (!settings.XiaoAI.OfficialEnabled)
                { Volatile.Write(ref status, "未启用官方技能连接"); await Task.Delay(3000, life.Token); continue; }
                var root = BaseAddress(settings.XiaoAI.RelayUrl);
                string token = tokenSource();
                if (token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new UserError("请保存不少于 32 字符且不含空白的电脑配对密钥。");
                if (!await interaction.CanAcceptSpeakerTaskAsync())
                { Volatile.Write(ref status, "等待当前电脑交互结束"); await Task.Delay(1000, life.Token); continue; }
                using var poll = Request(root, "v1/windows/lease", token);
                poll.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(poll, HttpCompletionOption.ResponseHeadersRead, life.Token);
                if (response.StatusCode == HttpStatusCode.NoContent)
                { Volatile.Write(ref status, "已连接 · 等待小爱任务"); await Task.Delay(1000, life.Token); continue; }
                response.EnsureSuccessStatusCode();
                string data = await ReadBoundedAsync(response, life.Token);
                var task = JsonSerializer.Deserialize<WindowsTask>(data, JsonOptions);
                if (task is null || task.JobId is not { Length: 64 } || task.Lease is not { Length: 64 } || !task.JobId.All(Uri.IsHexDigit) ||
                    !task.Lease.All(Uri.IsHexDigit) || task.Query is null or "" || task.Query.Length > 4096 ||
                    task.ExpiresAt <= DateTimeOffset.UtcNow || task.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(3))
                    throw new UserError("收到无效或过期的音箱任务。");
                Volatile.Write(ref status, "正在处理小爱任务");
                string reply, state;
                using var command = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
                command.CancelAfter(task.ExpiresAt - DateTimeOffset.UtcNow);
                try { reply = await interaction.ExecuteSpeakerTaskAsync(task.Query, command.Token); state = "completed"; }
                catch (OperationCanceledException) { reply = "电脑操作已取消，已执行的部分请在电脑查看。"; state = "cancelled"; }
                catch (UserError ex) { reply = ex.Message; state = "failed"; }
                catch (Exception) { reply = "电脑处理失败，请查看电脑上的执行记录。"; state = "failed"; }
                var result = new WindowsTaskResult(task.JobId, task.Lease, state,
                    reply.Length > 2000 ? reply[..1970] + "。完整结果请查看电脑。" : reply);
                bool acknowledged = false;
                for (int retry = 0; retry < 4 && !life.IsCancellationRequested; retry++)
                {
                    try
                    {
                        using var post = Request(root, "v1/windows/result", token);
                        post.Content = new StringContent(JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8, "application/json");
                        using var delivered = await http.SendAsync(post, life.Token);
                        delivered.EnsureSuccessStatusCode(); acknowledged = true; break;
                    }
                    catch (HttpRequestException) { await Task.Delay(1000, life.Token); }
                }
                Volatile.Write(ref status, acknowledged ? "结果已交回小爱技能" : "电脑已处理，结果未送达；不会自动重做");
            }
            catch (OperationCanceledException) when (life.IsCancellationRequested) { return; }
            catch (Exception) when (life.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UserError or IOException)
            { Volatile.Write(ref status, ex is UserError ? ex.Message : "技能服务暂不可用；未重复执行已领取任务"); }
            try { await Task.Delay(1500, life.Token); } catch (OperationCanceledException) { return; }
        }
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static HttpRequestMessage Request(Uri root, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return request;
    }
    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + count > 65536) throw new UserError("音箱任务超过大小限制。"); output.Write(buffer, 0, count); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public void Dispose() { life.Cancel(); http.Dispose(); }
}
