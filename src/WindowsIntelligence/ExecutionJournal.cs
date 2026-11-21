using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsIntelligence;

internal sealed record ExecutionStep(int Id, string Description, string Command, string Output, string Error,
    string State, int? ExitCode, double Seconds, bool ResultsWithheld = false);

// Session-only, bounded records. Nothing is added to the model's conversational
// history or written to disk. UI rendering never controls whether a command runs.
internal sealed class ExecutionJournal(params string[] secrets)
{
    private sealed class Entry(int id, string description, string command)
    {
        internal readonly int Id = id;
        internal readonly string Description = description, Command = command;
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly StringBuilder Output = new(), Error = new();
        internal string State = "正在执行";
        internal int? ExitCode;
    }
    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private const int Limit = 6100;
    internal int Version { get; private set; }

    internal int Begin(string command, string description)
    {
        lock (gate)
        {
            if (entries.Count >= 8) throw new UserError("当前任务已达到执行记录上限。");
            entries.Add(new(entries.Count + 1, description, command));
            Version++;
            return entries.Count;
        }
    }

    internal void Append(int id, bool error, string text)
    {
        lock (gate)
        {
            var entry = entries[id - 1];
            if (!entry.Clock.IsRunning) return;
            var target = error ? entry.Error : entry.Output;
            int count = Math.Min(text.Length, Math.Max(0, Limit - target.Length));
            if (count == 0) return;
            target.Append(text, 0, count);
            Version++;
        }
    }

    internal void Complete(int id, PowerShellResult? result, string? terminalState = null)
    {
        lock (gate)
        {
            var entry = entries[id - 1];
            entry.Clock.Stop();
            entry.ExitCode = result?.ExitCode;
            if (result is not null)
            {
                entry.Output.Clear().Append(result.Output[..Math.Min(result.Output.Length, Limit)]);
                entry.Error.Clear().Append(result.Error[..Math.Min(result.Error.Length, Limit)]);
            }
            entry.State = terminalState ?? (result?.TimedOut == true ? "已超时" : result?.ExitCode == 0 ? "已完成" : "执行失败");
            Version++;
        }
    }

    internal IReadOnlyList<ExecutionStep> Snapshot(bool revealResults = true)
    {
        lock (gate)
            return entries.Select(entry => new ExecutionStep(entry.Id, Redact(entry.Description), Redact(entry.Command),
                revealResults ? Redact(entry.Output.ToString(), entry.Clock.IsRunning) : "",
                revealResults ? Redact(entry.Error.ToString(), entry.Clock.IsRunning) : "",
                !revealResults && !entry.Clock.IsRunning ? "等待语音生成" : entry.State,
                revealResults ? entry.ExitCode : null, entry.Clock.Elapsed.TotalSeconds, !revealResults)).ToArray();
    }

    private string Redact(string value, bool streaming = false)
    {
        foreach (string secret in secrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
        {
            value = value.Replace(secret, "[密钥已隐藏]", StringComparison.Ordinal);
            // Hold back a credential prefix split between stdout reads.
            if (streaming)
                for (int length = Math.Min(secret.Length - 1, value.Length); length >= 4; length--)
                    if (value.EndsWith(secret[..length], StringComparison.Ordinal))
                    { value = value[..^length] + "[密钥已隐藏]"; break; }
        }
        return Regex.Replace(value, @"(?i)(\b(?:Bearer\s+|(?:api[_-]?key|password|token|secret)\s*[:=]\s*['"" ]*))[^\s'"";,}\]]+",
            "$1[已隐藏]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}
