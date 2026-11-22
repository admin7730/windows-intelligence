using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WindowsIntelligence;

internal sealed class ProviderUsage(HttpClient? transport = null, Func<Guid, string>? credential = null)
{
    internal static readonly Guid MiniMaxPlanCredential = new("61fd126e-b1b9-4a29-8a93-9dfe62c20545");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    internal async Task<string> QueryAsync(RememberedAction action, Settings settings, CancellationToken ct)
    {
        string Key(Guid id) => credential is null ? SecretStore.Get(id) : credential(id);
        if (action == RememberedAction.DeepSeekUsage)
        {
            // Select by the official origin, never send a key to an arbitrary configured host.
            var provider = settings.Providers.OrderByDescending(p => p.Id == settings.Provider.Id).FirstOrDefault(p => Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var url) &&
                url.Scheme == "https" && url.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo));
            if (provider is null) throw new UserError("请先在模型服务中配置官方 DeepSeek 账户，余额查询不支持第三方中转账户。");
            string key = Key(provider.Id);
            if (string.IsNullOrWhiteSpace(key)) throw new UserError("请先保存 DeepSeek API Key。");
            using var response = await GetAsync("https://api.deepseek.com/user/balance", key, ct);
            if (!response.RootElement.TryGetProperty("balance_infos", out var balances) || balances.ValueKind != JsonValueKind.Array || balances.GetArrayLength() == 0)
                throw new UserError("DeepSeek 未返回有效余额数据。");
            var result = new StringBuilder(L10n.T("DeepSeek 当前账户余额：") + "\n");
            foreach (var balance in balances.EnumerateArray().Take(5))
                result.AppendLine(L10n.F("- {0}：可用 {1}，充值余额 {2}，赠金 {3}。", "- {0}: available {1}; topped up {2}; granted {3}.", Amount(balance,"currency"),Amount(balance,"total_balance"),Amount(balance,"topped_up_balance"),Amount(balance,"granted_balance")));
            result.AppendLine(L10n.F("此接口返回当前余额，不包含历史 Token 用量或消费账单。历史用量请查看官方控制台：{0}", "This API returns the current balance, not historical token usage or billing. View usage: {0}","https://platform.deepseek.com/usage"));
            result.AppendLine(L10n.F("来源：{0} · 查询时间：{1:g}","Source: {0} · Retrieved: {1:g}","https://api.deepseek.com/user/balance",DateTimeOffset.Now));
            return result.ToString();
        }
        if (action == RememberedAction.MiniMaxUsage)
        {
            if (!settings.Memory.MiniMaxSubscription)
                return L10n.F("MiniMax 语音按量账户的余额、资源包和历史用量需在官方控制台查询：{0} 。当前没有接入经过验证的普通语音账户余额 API，不能仅凭语音 Key 返回余额。若你使用国际站 Token Plan，可在“对话与记忆”中启用套餐查询并单独保存订阅 Key；套餐额度不等于现金余额。",
                    "MiniMax pay-as-you-go speech balance, packages and usage require the official console: {0}. No verified speech-account balance API is connected; a speech key alone cannot return your cash balance. For an international Token Plan, enable quota queries in Conversations & memory and save a separate subscription key. Quota is not cash balance.","https://platform.minimax.cn/");
            string key = Key(MiniMaxPlanCredential);
            if (string.IsNullOrWhiteSpace(key)) throw new UserError("请在“对话与记忆”保存 MiniMax 国际站 Token Plan 订阅 Key，语音按量 Key 不通用。");
            using var response = await GetAsync("https://www.minimax.io/v1/token_plan/remains", key, ct);
            if (response.RootElement.TryGetProperty("base_resp", out var status) && status.TryGetProperty("status_code", out var code) && code.ToString() != "0")
                throw new UserError("MiniMax 套餐查询失败，请检查订阅 Key 和账户类型。");
            // Report a bounded set of account/quota values only; no raw response, identity or token data.
            var values = new List<string>();
            ReadQuotas(response.RootElement, "", values, 0);
            if (values.Count == 0) throw new UserError("MiniMax 未返回可识别的套餐额度字段，未推断剩余额度或余额。");
            return L10n.T("MiniMax 国际站 Token Plan 查询结果（套餐额度，不是语音按量现金余额）：") + "\n" + string.Join("\n", values.Take(30)) + "\n" + L10n.T("查询时间：") + DateTimeOffset.Now.ToString("g");
        }
        throw new UserError("此规则不是用量查询。");
    }

    private async Task<JsonDocument> GetAsync(string endpoint, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await (transport ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UserError($"官方账户查询失败（HTTP {(int)response.StatusCode}），未返回余额。请检查凭据、账户类型或稍后重试。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 128 * 1024) throw new UserError("账户查询结果过大，已停止读取。");
            buffer.Write(chunk, 0, read);
        }
        try { return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new UserError("官方账户查询返回格式无效，未推断余额。"); }
    }

    private static string Amount(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return "未提供";
        var text = value.ToString();
        return text.Length <= 32 ? text : "格式无效";
    }
    private static void ReadQuotas(JsonElement item, string prefix, List<string> values, int depth)
    {
        if (depth > 8 || values.Count >= 30) return;
        if (item.ValueKind == JsonValueKind.Array)
        {
            int i = 0; foreach (var child in item.EnumerateArray().Take(10)) ReadQuotas(child, prefix + $"[{++i}]", values, depth + 1);
        }
        else if (item.ValueKind == JsonValueKind.Object)
            foreach (var field in item.EnumerateObject())
            {
                if (field.Name.Length > 80 || field.Name.Contains("token", StringComparison.OrdinalIgnoreCase) && !field.Name.Contains("remain", StringComparison.OrdinalIgnoreCase) && !field.Name.Contains("total", StringComparison.OrdinalIgnoreCase) ||
                    new[] { "key", "secret", "user", "device", "email", "phone", "authorization" }.Any(x => field.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                string path = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
                if (field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) ReadQuotas(field.Value, path, values, depth + 1);
                else if (field.Value.ValueKind == JsonValueKind.Number && new[] { "remain", "used", "usage", "total", "limit", "quota", "credit", "count", "reset", "end_time", "start_time" }.Any(x => field.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    values.Add("- " + path + "：" + field.Value);
            }
    }
}
