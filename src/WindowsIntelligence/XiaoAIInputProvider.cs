using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsIntelligence;

internal enum XiaoAIRoute { NativeXiaoAI, ObserveWindows, ForwardWindows, Rejected }
internal sealed record XiaoAIFeasibility(bool NativeAsrObserved = false, bool BeforeExecutionHandoffVerified = false,
    bool PlaybackCoexistenceVerified = false, string Firmware = "", string EvidenceId = "")
{
    internal bool SeamlessVerified => NativeAsrObserved && BeforeExecutionHandoffVerified && PlaybackCoexistenceVerified &&
        Firmware.Length > 0 && EvidenceId.Length > 0;
}
internal sealed record XiaoAIRouteResult(XiaoAIRoute Route, string Code, string Reply = "");
// Transport must bind this acknowledgement to the authenticated device and this exact event.
// No production transport currently creates receipts; tests only simulate the protocol boundary.
internal sealed record XiaoAIHandoffReceipt(string DeviceId, string EventId, DateTimeOffset ExpiresAt,
    bool NativeSuppressionAcknowledged);

// Adapter for the existing Interaction/AiClient pipeline. No independent agent.
// A log observation cannot enable an unverified native-command takeover.
internal sealed class XiaoAIInputProvider
{
    private readonly XiaoAISettings settings;
    private readonly XiaoAIFeasibility evidence;
    private readonly XiaoAIEventGate events;
    private readonly Func<AssistantInput,CancellationToken,Task<string>> execute;
    internal XiaoAIDiagnostics Diagnostics { get; } = new();
    internal XiaoAIFeasibility Evidence => evidence;
    internal XiaoAIInputProvider(XiaoAISettings settings, Func<AssistantInput,CancellationToken,Task<string>> execute,
        XiaoAIFeasibility? evidence = null, DateTimeOffset? started = null)
    {
        this.settings = settings; this.execute = execute;
        this.evidence = evidence ?? new();
        events = new(started ?? DateTimeOffset.UtcNow);
    }
    internal static string? ExtractWindowsRequest(string text, IEnumerable<string> phrases)
    {
        string value = text.Trim();
        if (value.StartsWith("小爱同学",StringComparison.Ordinal)) value = value[4..].TrimStart('，',',',' ','。');
        foreach (string phrase in phrases.Where(p => !string.IsNullOrWhiteSpace(p)).OrderByDescending(p => p.Length))
            if (value.Contains(phrase,StringComparison.OrdinalIgnoreCase) &&
                (value.StartsWith("电脑助手",StringComparison.Ordinal) ||
                 Regex.IsMatch(value,"打开|启动|关闭|查询|查看|检查|占用|多少|读取|写入|创建|整理|搜索|调整|设置|截图|运行|安装|卸载|复制|移动|删除|重命名|暂停|继续|切换|播放"))) return value;
        return null;
    }
    internal async Task<XiaoAIRouteResult> ReceiveAsync(AssistantInput input, bool authenticatedEncryptedChannel, CancellationToken ct,
        XiaoAIHandoffReceipt? handoff = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!settings.Enabled) return new(XiaoAIRoute.Rejected,"disabled");
        if (!authenticatedEncryptedChannel) return new(XiaoAIRoute.Rejected,"untrusted-channel");
        if (!events.Accept(input,settings.DeviceId,DateTimeOffset.UtcNow)) return new(XiaoAIRoute.Rejected,"stale-duplicate-or-device");
        if (ExtractWindowsRequest(input.Text,settings.TriggerPhrases) is null)
        {
            Diagnostics.Add("route","native",input.EventId);
            return new(XiaoAIRoute.NativeXiaoAI,"native");
        }
        if (!settings.ComputerControl || !evidence.SeamlessVerified || evidence.Firmware != settings.DeviceFirmware)
        {
            Diagnostics.Add("route","awaiting-A-B-C",input.EventId);
            return new(XiaoAIRoute.ObserveWindows,"awaiting-A-B-C");
        }
        if (handoff is not { NativeSuppressionAcknowledged: true } || handoff.DeviceId != input.DeviceId ||
            handoff.EventId != input.EventId || handoff.ExpiresAt <= DateTimeOffset.UtcNow ||
            handoff.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            Diagnostics.Add("route","missing-native-handoff",input.EventId);
            return new(XiaoAIRoute.ObserveWindows,"missing-native-handoff");
        }
        if (Regex.IsMatch(input.Text,@"删除|格式化|注册表|关机|重启|锁屏|管理员|PowerShell|cmd\.exe|执行.{0,6}脚本|修改.{0,8}(系统|配置|密码)",RegexOptions.IgnoreCase))
        {
            Diagnostics.Add("policy","local-confirmation-required",input.EventId);
            return new(XiaoAIRoute.Rejected,"local-confirmation-required","此操作需要在电脑上确认。");
        }
        string reply = await execute(input,ct).ConfigureAwait(false);
        Diagnostics.Add("route","windows",input.EventId);
        return new(XiaoAIRoute.ForwardWindows,"windows",reply);
    }
    internal static AssistantInput? ParseNativeInstruction(string line, XiaoAIDevice device, DateTimeOffset receivedAt)
    {
        if (line.Length > 65536 || !XiaoAIDevice.IsSupported(device.Model)) return null;
        try
        {
            using var json = JsonDocument.Parse(line,new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (!root.TryGetProperty("header",out var header) || !root.TryGetProperty("payload",out var payload) ||
                header.GetProperty("namespace").GetString() != "SpeechRecognizer" || header.GetProperty("name").GetString() != "RecognizeResult" ||
                !payload.TryGetProperty("is_final",out var final) || final.ValueKind != JsonValueKind.True ||
                !payload.TryGetProperty("results",out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
            string text = results[0].GetProperty("text").GetString() ?? "";
            string id = header.TryGetProperty("id",out var eventId) ? eventId.GetString() ?? "" : "";
            if (text.Length is 0 or >4096 || id.Length == 0) return null;
            // Actual receipt time, not a firmware timestamp or proof of pre-execution interception.
            return new(AssistantInputKind.XiaoAISpeaker,text,receivedAt,device.Id,device.Model,id);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
}
