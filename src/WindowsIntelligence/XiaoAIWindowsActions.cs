using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsIntelligence;

// Remote requests never receive the local assistant's arbitrary PowerShell callback.
internal sealed class XiaoAIWindowsActions(XiaoAISettings settings,
    Func<string,CancellationToken,Task<string>> ask,
    Func<string,CancellationToken,Task<PowerShellResult>>? systemQuery = null,
    Action<ProcessStartInfo>? launch = null)
{
    private readonly Func<string,CancellationToken,Task<PowerShellResult>> query = systemQuery ??
        ((script,ct) => PowerShellRunner.RunAsync(script,ct));
    private readonly Action<ProcessStartInfo> start = launch ?? (info => Process.Start(info));
    internal async Task<string> ExecuteAsync(AssistantInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input.Source != AssistantInputKind.XiaoAISpeaker || !settings.ComputerControl) throw new UserError("音箱电脑控制尚未授权。");
        string text = input.Text;
        if (Regex.IsMatch(text,@"删除|格式化|注册表|关机|重启|锁屏|管理员|PowerShell|cmd\.exe|执行.{0,6}脚本|修改.{0,8}(系统|配置|密码)",RegexOptions.IgnoreCase))
            return "此操作需要在电脑上确认，音箱不会直接执行。";
        if (text.Contains("内存",StringComparison.Ordinal) && Regex.IsMatch(text,"查询|查看|多少|占用|使用"))
        {
            const string script = "$os=Get-CimInstance Win32_OperatingSystem; @{totalKiB=$os.TotalVisibleMemorySize;freeKiB=$os.FreePhysicalMemory}|ConvertTo-Json -Compress";
            var result = await query(script,ct).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new UserError("系统内存查询失败。");
            using var json = JsonDocument.Parse(result.Output);
            double total = json.RootElement.GetProperty("totalKiB").GetDouble(), free = json.RootElement.GetProperty("freeKiB").GetDouble();
            if (total <= 0 || free < 0 || free > total) throw new UserError("系统内存数据无效。");
            return $"当前电脑内存使用率约为百分之{(total-free)*100/total:F0}。";
        }
        if (Regex.IsMatch(text,"打开|启动"))
        {
            foreach (var app in settings.Applications.Where(a => text.Contains(a.Key,StringComparison.OrdinalIgnoreCase)))
            {
                string path = Path.GetFullPath(app.Value);
                if (!File.Exists(path) || !path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) ||
                    new[] {"cmd","powershell","pwsh","wscript","cscript","mshta"}.Contains(Path.GetFileNameWithoutExtension(path),StringComparer.OrdinalIgnoreCase))
                    throw new UserError("该程序不在可启动的应用白名单中。");
                start(new ProcessStartInfo(path) { UseShellExecute = false });
                return "已启动 " + app.Key + "。";
            }
            foreach (var site in settings.Websites.Where(s => text.Contains(s.Key,StringComparison.OrdinalIgnoreCase)))
            {
                if (!Uri.TryCreate(site.Value,UriKind.Absolute,out var url) || url.Scheme != "https" || url.UserInfo.Length != 0)
                    throw new UserError("网站白名单需要不含凭据的 HTTPS 地址。");
                start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                return "已打开 " + site.Key + "。";
            }
        }
        if (!settings.NaturalLanguage) return "这条指令尚未配置允许的操作。";
        // The same configured AiClient answers complex requests; no unrestricted remote shell is offered.
        return await ask(text,ct).ConfigureAwait(false);
    }
}
