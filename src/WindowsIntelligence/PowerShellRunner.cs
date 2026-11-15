using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WindowsIntelligence;

internal sealed record PowerShellResult(int? ExitCode, string Output, string Error, bool TimedOut = false)
{
    public string Describe()
    {
        var result = new StringBuilder(TimedOut ? "PowerShell 执行超时，已停止等待。" :
            ExitCode is { } code ? $"PowerShell 已退出，退出码 {code}。" : "无法启动 PowerShell。");
        foreach (string text in new[] { Output, Error })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.AppendLine().AppendLine();
            foreach (string line in text.TrimEnd().Split('\n')) result.Append("    ").AppendLine(line.TrimEnd('\r'));
        }
        return result.ToString().TrimEnd();
    }
}

internal static class PowerShellRunner
{
    internal const int MaxCommandLength = 4000;
    private const int MaxOutputLength = 6000;

    public static async Task<PowerShellResult> RunAsync(string command, CancellationToken cancellation, TimeSpan? limit = null)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > MaxCommandLength || command.Contains('\0'))
            throw new UserError("PowerShell 命令为空、包含无效字符或超过长度限制。");
        cancellation.ThrowIfCancellationRequested();
        string body = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string wrapper = $$"""
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $ErrorActionPreference = 'Stop'
            try {
                & ([ScriptBlock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{body}}'))))
                exit ([int]$LASTEXITCODE)
            } catch {
                [Console]::Error.WriteLine($_.ToString())
                exit 1
            }
            """;
        // The command is data inside the wrapper. An unmatched quote or brace cannot change the wrapper.
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text",
            "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper)) })
            start.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(60));
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { return new(null, "", ex.Message); }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token),
                DrainAsync(process.StandardOutput, stdout, timeout.Token),
                DrainAsync(process.StandardError, stderr, timeout.Token)).ConfigureAwait(false);
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            cancellation.ThrowIfCancellationRequested();
            return new(null, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }
    }

    private static async Task DrainAsync(StreamReader source, StringBuilder output, CancellationToken cancellation)
    {
        var buffer = new char[2048];
        bool truncated = false;
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
        {
            int retain = Math.Min(count, MaxOutputLength - output.Length);
            output.Append(buffer, 0, retain);
            truncated |= retain < count;
        }
        if (truncated) output.AppendLine().Append("[输出已截断]");
    }
}
