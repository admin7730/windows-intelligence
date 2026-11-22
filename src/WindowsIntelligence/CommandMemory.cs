using System.Text.RegularExpressions;

namespace WindowsIntelligence;

internal enum RememberedAction { DeepSeekUsage, MiniMaxUsage, AssistantTask }
internal sealed class RememberedCommand
{
    public bool Enabled { get; set; } = true;
    public string Phrases { get; set; } = "";
    public RememberedAction Action { get; set; }
    public string Task { get; set; } = "";
}
internal sealed class MemorySettings
{
    public int ConversationMinutes { get; set; } = 15;
    public string Personal { get; set; } = "";
    public bool MiniMaxSubscription { get; set; }
    public List<RememberedCommand> Commands { get; set; } =
    [
        new() { Phrases = "查询 DeepSeek 用量|查询 DeepSeek 余额|DeepSeek 用量|DeepSeek 余额|Check DeepSeek usage|Check DeepSeek balance", Action = RememberedAction.DeepSeekUsage },
        new() { Phrases = "查询 MiniMax 用量|查询 MiniMax 余额|MiniMax 用量|MiniMax 余额|Check MiniMax usage|Check MiniMax balance", Action = RememberedAction.MiniMaxUsage }
    ];
}

internal static class CommandMemory
{
    private static string Normalize(string text) => new(L10n.MapChinese(text.Normalize(),false).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    internal static RememberedCommand? Match(string text, MemorySettings memory)
    {
        string value = Normalize(text);
        return memory.Commands.Take(50).FirstOrDefault(rule => rule.Enabled && rule.Phrases.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(phrase => Normalize(phrase) is { Length: > 0 } normalized && value == normalized));
    }
    internal static void Validate(MemorySettings memory)
    {
        if (memory.Personal.Length > 8000 || memory.Commands.Count > 50) throw new UserError("个人记忆最多 8000 字，指令规则最多 50 条。");
        var phrases = new HashSet<string>();
        foreach (var rule in memory.Commands.Where(r => r.Enabled))
        {
            if (!Enum.IsDefined(rule.Action) || rule.Phrases.Length > 1000 || rule.Task.Length > 4000) throw new UserError("指令记忆格式或长度无效。");
            if (rule.Action == RememberedAction.AssistantTask && string.IsNullOrWhiteSpace(rule.Task)) throw new UserError("请填写要交给助手的任务。");
            var names = rule.Phrases.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0 || names.Any(name => Normalize(name).Length < 2)) throw new UserError("每条规则需要至少一个有效触发词。");
            foreach (var phrase in names) if (!phrases.Add(Normalize(phrase))) throw new UserError("有重复的触发词，请只保留一条对应规则。");
        }
    }
}
