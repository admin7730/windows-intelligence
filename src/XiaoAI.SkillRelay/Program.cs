using System.Security.Cryptography;
using System.Text;
using WindowsIntelligence.SkillRelay;
using WindowsIntelligence.Xiaomi;

var app = RelayHost.Build(RelayOptions.FromEnvironment());
await app.RunAsync();

namespace WindowsIntelligence.SkillRelay
{
    public sealed record RelayOptions(string AppId, string KeyId, byte[] Secret, string WindowsToken, string UserId,
        string DeviceId, string PublicHost, string StatePath, bool LoopbackTest = false)
    {
        public static RelayOptions FromEnvironment()
        {
            string Read(string key) => Environment.GetEnvironmentVariable("WI_XIAOAI_" + key) ?? "";
            return new(Read("APP_ID"), Read("KEY_ID"), Convert.FromBase64String(Read("SECRET_BASE64")),
                Read("WINDOWS_TOKEN"), Read("USER_ID"), Read("DEVICE_ID"), Read("PUBLIC_HOST"), Read("STATE_PATH"));
        }
        public void Validate()
        {
            if (new[] { AppId, KeyId, WindowsToken, UserId, DeviceId, PublicHost, StatePath }.Any(string.IsNullOrWhiteSpace) ||
                Secret.Length < 16 || WindowsToken.Length < 32 || PublicHost.Contains('/') || PublicHost.Contains('\n'))
                throw new InvalidOperationException("Configure the skill ID, signing key, paired user/device, public host, state path and Windows token before starting.");
        }
    }
    public static class RelayHost
    {
        public static WebApplication Build(RelayOptions options)
        {
            options.Validate();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); // No request URLs, transcripts or credentials in default access logs.
            var app = builder.Build(); var store = new RelayStore(options.StatePath);
            if (!options.LoopbackTest)
                app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
                { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
            bool Transport(HttpContext context) => context.Request.IsHttps || options.LoopbackTest &&
                context.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);
            bool Windows(HttpContext context)
            {
                string supplied = context.Request.Headers.Authorization.ToString();
                return Transport(context) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),
                    Encoding.UTF8.GetBytes("Bearer " + options.WindowsToken));
            }
            app.MapPost("/xiaomi/skill", async (HttpContext context) =>
            {
                if (!Transport(context) || context.Request.QueryString.HasValue) return Results.Unauthorized();
                byte[] body;
                using (var data = new MemoryStream())
                {
                    var buffer = new byte[4096]; int count;
                    while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    { if (data.Length + count > 65536) return Results.StatusCode(413); data.Write(buffer, 0, count); }
                    body = data.ToArray();
                }
                string Header(string name) => context.Request.Headers[name].ToString();
                string host = Header("x-original-host");
                if (!string.Equals(host, options.PublicHost, StringComparison.OrdinalIgnoreCase) ||
                    !XiaomiSignature.Verify(options.Secret, options.KeyId, Header("Authorization"), "/xiaomi/skill",
                        Header("X-Xiaomi-Date") is { Length: > 0 } date ? date : Header("Date"), host,
                        context.Request.ContentType ?? "", Header("Content-MD5"), body, DateTimeOffset.UtcNow)) return Results.Unauthorized();
                SkillRequest request;
                try { request = SkillRequest.Parse(body); }
                catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException or ArgumentOutOfRangeException)
                { return Results.BadRequest(); }
                if (request.AppId != options.AppId || request.UserId != options.UserId || request.DeviceId != options.DeviceId ||
                    Math.Abs((DateTimeOffset.UtcNow - request.Timestamp).TotalSeconds) > 120) return Results.Unauthorized();
                if (request.Type == 2) return Results.Json(SkillReplies.Speak("已退出电脑助手。"));
                if (SkillReplies.IsResultQuery(request))
                {
                    var previous = store.Find(request.Owner, request.PreviousJob);
                    return Results.Json(SkillReplies.Speak(previous is null ? "当前没有可查询的电脑任务。" : Reply(previous), jobId: previous?.Id));
                }
                if (!SkillReplies.IsComputerRequest(request))
                    return Results.Json(SkillReplies.Speak("这项请退出电脑助手后让小爱处理。", notUnderstood: true));
                RelayJob job;
                try { job = store.Submit(request, DateTimeOffset.UtcNow); }
                catch (InvalidOperationException) { return Results.Json(SkillReplies.Speak("电脑任务较多，请稍后再试。")); }
                var until = DateTimeOffset.UtcNow.AddMilliseconds(1800);
                while (job.State is "queued" or "leased" && DateTimeOffset.UtcNow < until)
                {
                    await Task.Delay(50, context.RequestAborted);
                    job = store.Find(request.Owner, job.Id)!;
                }
                return Results.Json(SkillReplies.Speak(Reply(job), jobId: job.Id));
            });
            app.MapPost("/v1/windows/lease", (HttpContext context) => !Windows(context) ? Results.Unauthorized() :
                store.Lease(DateTimeOffset.UtcNow) is { } job ? Results.Json(job) : Results.NoContent());
            app.MapPost("/v1/windows/result", async (HttpContext context) =>
            {
                if (!Windows(context)) return Results.Unauthorized();
                if (context.Request.ContentLength is null or > 16384) return Results.StatusCode(413);
                WindowsTaskResult? result;
                try { result = await context.Request.ReadFromJsonAsync<WindowsTaskResult>(context.RequestAborted); }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
                if (result is null || result.JobId is not { Length: 64 } || result.Lease is not { Length: 64 } ||
                    result.State is null || result.Reply is null) return Results.BadRequest();
                return store.Complete(result) ? Results.Ok() : Results.BadRequest();
            });
            return app;
        }
        private static string Reply(RelayJob job) => job.State is "queued" or "leased" ?
            "任务正在电脑上处理。稍后可以说，用电脑助手查看结果。" : job.Reply.Length == 0 ? "没有收到可播报的结果。" : job.Reply;
    }
}
