using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace WindowsIntelligence;

internal static class DispatchTools
{
    internal static string Harness(DispatchSettings options) => Find(options.HarnessCommand,"dsh.cmd",
        ProcessPaths("DeepSeek Harness").Select(p=>Path.Combine(Path.GetDirectoryName(p)!,"resources/runtime/cli/bin/dsh.cmd"))
        .Concat(new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs/DeepSeek Harness/resources/runtime/cli/bin/dsh.cmd"),@"D:\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"}));
    internal static string Codex(DispatchSettings options) => Find(options.CodexCommand,"codex.exe",
        ProcessPaths("ChatGPT").Select(p=>Path.Combine(Path.GetDirectoryName(p)!,"resources/codex.exe"))
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft/WindowsApps/codex.exe")));
    private static IEnumerable<string> ProcessPaths(string name)
    {
        foreach(var process in Process.GetProcessesByName(name))
        {
            string? path=null; try{path=process.MainModule?.FileName;}catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){}finally{process.Dispose();}
            if(path is not null)yield return path;
        }
    }
    private static string Find(string configured,string command,IEnumerable<string> candidates)
    {
        if(!string.IsNullOrWhiteSpace(configured))return Valid(configured)?Path.GetFullPath(configured):"";
        foreach(var candidate in (Environment.GetEnvironmentVariable("PATH")??"").Split(';').Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>Path.Combine(p.Trim('"'),command)).Concat(candidates))
            if(Valid(candidate))return Path.GetFullPath(candidate);
        return "";
    }
    private static bool Valid(string path)=>Path.IsPathFullyQualified(path) && File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat";
    internal static ProcessStartInfo Start(string command,IReadOnlyList<string> arguments,string workspace)
    {
        if(!Valid(command))throw new UserError("找不到工具命令，请先安装或在任务派发设置中指定路径。");
        var start=new ProcessStartInfo {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=workspace,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        if(Path.GetExtension(command).Equals(".exe",StringComparison.OrdinalIgnoreCase))
        {
            start.FileName=command;foreach(var arg in arguments)start.ArgumentList.Add(arg);
        }
        else
        {
            static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
            string script="$OutputEncoding=[Console]::InputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); & "+Quote(command)+" "+string.Join(" ",arguments.Select(Quote))+"; exit $LASTEXITCODE";
            start.FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe");
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        }
        start.Environment.Remove("CODEX_THREAD_ID");
        return start;
    }
}

internal sealed class DispatchEvents(DispatchTarget target,Action<string> progress,Action<string> session)
{
    internal string Session {get;private set;}="";
    internal string Answer {get;private set;}="";
    internal bool Completed {get;private set;}
    internal bool Failed {get;private set;}
    internal void Read(string line)
    {
        JsonDocument doc;
        try{doc=JsonDocument.Parse(line,new JsonDocumentOptions{MaxDepth=40});}catch(JsonException){return;}
        using(doc)
        {
            var value=doc.RootElement;
            string type=Text(value,"type");
            if(target==DispatchTarget.DeepSeekHarness)
            {
                if(type is "thinking" or "reasoning")return;
                if(type=="session")SetSession(Text(value,"sessionId"));
                else if(type=="text") {string text=Text(value,"text");Answer=TaskDispatch.Bound(Answer+text,32000);progress(text);}
                else if(type=="tool_call")
                {
                    string tool=Text(value,"tool");progress("工具："+tool);
                    if(value.TryGetProperty("input",out var input) && input.ValueKind==JsonValueKind.Object)
                        foreach(string name in new[]{"command","path","file_path"})if(input.TryGetProperty(name,out var field))progress(name+": "+TaskDispatch.Bound(field.ToString(),2000));
                }
                else if(type=="tool_result")
                {
                    progress("工具返回："+Text(value,"status"));
                    if(value.TryGetProperty("result",out var result))progress(TaskDispatch.Bound(result.ToString(),3000));
                }
                else if(type=="final")
                {
                    SetSession(Text(value,"sessionId"));
                    string kind=value.TryGetProperty("reason",out var reason)?Text(reason,"kind"):"";
                    Completed=kind=="completed";Failed=!Completed;
                    Answer=Text(value,"text");
                    if(Failed)progress("Harness 未完成任务："+kind);
                }
            }
            else
            {
                if(type=="thread.started")SetSession(Text(value,"thread_id"));
                else if(type=="turn.completed")Completed=true;
                else if(type is "turn.failed" or "error") {Failed=true;progress("Codex 执行未完成："+TaskDispatch.Bound(value.TryGetProperty("error",out var error)?error.ToString():Text(value,"message"),2000));}
                else if(type.StartsWith("item.",StringComparison.Ordinal) && value.TryGetProperty("item",out var item))
                {
                    string kind=Text(item,"type");
                    if(kind=="agent_message" && type=="item.completed") {Answer=Text(item,"text");progress(Answer);}
                    else if(kind=="command_execution")
                    {
                        progress("命令："+Text(item,"command"));
                        if(type=="item.completed")progress("返回："+TaskDispatch.Bound(Text(item,"aggregated_output"),4000)+" · "+Text(item,"exit_code"));
                    }
                    else if(kind=="file_change" && item.TryGetProperty("changes",out var changes)) progress("文件变更："+TaskDispatch.Bound(changes.ToString(),2000));
                    else if(kind=="mcp_tool_call")progress("工具："+Text(item,"server")+"/"+Text(item,"tool"));
                    // Reasoning events and unknown model-internal fields are intentionally not surfaced or saved.
                }
            }
        }
    }
    private void SetSession(string id){if(TaskDispatch.ValidSession(target,id)){Session=id;session(id);}}
    private static string Text(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object && value.TryGetProperty(name,out var field) && field.ValueKind is JsonValueKind.String or JsonValueKind.Number?field.ToString():"";
}

internal sealed class ExternalTaskRunner(Settings settings,HttpClient? transport=null,Func<Guid,string>? credential=null) : IDispatchRunner
{
    internal static readonly Guid WorkBuddyCredential=new("ca4bdbb7-fda5-4d57-9e8e-9369046b3502");
    private static readonly HttpClient Http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(35)};
    private string Key(Guid id)=>credential is null?SecretStore.Get(id):credential(id);
    public async Task<DispatchOutcome> RunAsync(DispatchJob job,Action<string> progress,Action<string> session,CancellationToken ct)
    {
        if(job.Target==DispatchTarget.WorkBuddyCloud)return await SubmitCloud(job,ct).ConfigureAwait(false);
        TaskDispatch.ValidateWorkspace(job.Workspace);
        string executable=job.Target==DispatchTarget.DeepSeekHarness?DispatchTools.Harness(settings.Dispatch):DispatchTools.Codex(settings.Dispatch);
        if(executable.Length==0)throw new UserError("此目标尚未安装或找不到命令，请在设置中检查路径。");
        string mode=job.ReadOnly?"read-only":"workspace-write";
        var args=new List<string>();
        if(job.Target==DispatchTarget.DeepSeekHarness)
        {
            args.AddRange(["headless","--json","--permission-mode",mode,"--approval-policy","never"]);
            if(job.ResumeSession.Length>0)args.AddRange(["--session-id",job.ResumeSession]);args.Add("-");
        }
        else
        {
            args.AddRange(["exec","--json","--color","never","--skip-git-repo-check","--sandbox",mode,"-C",job.Workspace]);
            if(job.ResumeSession.Length>0)args.AddRange(["resume",job.ResumeSession]);args.Add("-");
        }
        var start=DispatchTools.Start(executable,args,job.Workspace);
        string secret="";
        if(job.Target==DispatchTarget.DeepSeekHarness && settings.Dispatch.UseDeepSeekCredential)
        {
            var provider=settings.Providers.OrderByDescending(p=>p.Id==settings.Provider.Id).FirstOrDefault(p=>Uri.TryCreate(p.BaseUrl,UriKind.Absolute,out var url) && url.Scheme=="https" && url.Host=="api.deepseek.com" && url.IsDefaultPort && url.UserInfo.Length==0);
            if(provider is not null)secret=Key(provider.Id);
            if(secret.Length>0)start.Environment["DEEPSEEK_API_KEY"]=secret;
        }
        string Clean(string text)=>TaskDispatch.Redact(secret.Length>0?text.Replace(secret,"[凭据已隐藏]",StringComparison.Ordinal):text);
        var events=new DispatchEvents(job.Target,text=>progress(Clean(text)),session);
        using var process=new Process{StartInfo=start};
        ct.ThrowIfCancellationRequested();
        if(!process.Start())throw new UserError("无法启动目标工具。");
        using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){}});
        progress("工作目录："+job.Workspace+"\n执行器："+executable+"\n权限："+mode);
        var stdout=Lines(process.StandardOutput,line=>events.Read(line),ct);
        var stderr=Lines(process.StandardError,line=>progress(Clean(TaskDispatch.Bound(line,2000))),ct);
        try
        {
            await process.StandardInput.WriteAsync(job.Prompt.AsMemory(),ct).ConfigureAwait(false);process.StandardInput.Close();
            await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(ct)).ConfigureAwait(false);
        }
        catch
        {
            try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(Exception ex)when(ex is InvalidOperationException or System.ComponentModel.Win32Exception){}
            throw;
        }
        ct.ThrowIfCancellationRequested();
        progress("进程退出码："+process.ExitCode);
        if(process.ExitCode!=0 || !events.Completed || events.Failed)
            return new(DispatchState.Failed,"工具未确认完成任务；请查看执行记录。退出码："+process.ExitCode,events.Session);
        if (job.ResumeSession.Length > 0 && !events.Session.Equals(job.ResumeSession, StringComparison.OrdinalIgnoreCase))
            return new(DispatchState.Unknown, "工具返回的会话与所选对话不一致，请检查原生会话。", events.Session);
        return new(DispatchState.Completed,Clean(events.Answer),events.Session);
    }
    private static async Task Lines(StreamReader reader,Action<string> receive,CancellationToken ct)
    {
        var buffer=new char[4096];var line=new StringBuilder();bool tooLong=false;int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),ct).ConfigureAwait(false))>0)
            for(int i=0;i<count;i++)
            {
                char c=buffer[i];if(c=='\n'){if(!tooLong)receive(line.ToString().TrimEnd('\r'));line.Clear();tooLong=false;}
                else if(line.Length<128*1024)line.Append(c);else tooLong=true;
            }
        if(line.Length>0 && !tooLong)receive(line.ToString());
    }
    private async Task<DispatchOutcome> SubmitCloud(DispatchJob job,CancellationToken ct)
    {
        if(job.ResumeSession.Length>0)throw new UserError("云端续聊请在 WorkBuddy 中进行。");
        string key=CloudKey();
        using var request=new HttpRequestMessage(HttpMethod.Post,"https://www.workbuddy.cn/openapi/v2/tasks");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=JsonContent.Create(new{prompt=job.Prompt,name=job.Title});
        try
        {
            using var response=await (transport??Http).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
            if((int)response.StatusCode>=500)throw new DispatchUncertain("WorkBuddy 服务异常，无法确认是否接收，未自动重发。");
            if(!response.IsSuccessStatusCode)throw new UserError("WorkBuddy 请求失败（HTTP "+(int)response.StatusCode+"），未自动重发。");
            using var json=await ReadJson(response,ct,submission:true).ConfigureAwait(false);
            if(!json.RootElement.TryGetProperty("task_id",out var id) || id.ValueKind!=JsonValueKind.String || !ValidCloudId(id.GetString()!))throw new DispatchUncertain("WorkBuddy 响应缺少有效任务 ID，无法确认接收结果，未自动重发。");
            return new(DispatchState.Submitted,"WorkBuddy 已接收云端任务；这不代表执行完成。云端沙箱不能直接访问本机工作目录，可刷新远端状态。",id.GetString()!);
        }
        catch(HttpRequestException){throw new DispatchUncertain("连接中断，无法确认 WorkBuddy 是否已接收，未自动重发。");}
        catch(JsonException){throw new DispatchUncertain("WorkBuddy 响应格式无效，无法确认接收结果，未自动重发。");}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw new DispatchUncertain("已停止本地等待，WorkBuddy 可能已接收；不会自动重发或宣称远端已取消。");}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new DispatchUncertain("请求超时，无法确认 WorkBuddy 是否已接收，未自动重发。");}
    }
    public async Task<DispatchOutcome> RefreshAsync(DispatchJob job,CancellationToken ct)
    {
        if(!ValidCloudId(job.RemoteId))throw new UserError("无效的远端任务 ID。");
        using var request=new HttpRequestMessage(HttpMethod.Get,"https://www.workbuddy.cn/openapi/v2/tasks/"+Uri.EscapeDataString(job.RemoteId));
        string key=CloudKey();
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        using var response=await (transport??Http).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new UserError("无法查询 WorkBuddy 状态（HTTP "+(int)response.StatusCode+"）。");
        using var json=await ReadJson(response,ct).ConfigureAwait(false);
        string status=json.RootElement.TryGetProperty("status",out var field)?field.GetString()??"":"";
        return status switch
        {
            "completed"=>new(DispatchState.Completed,"WorkBuddy 报告远端已完成。此接口不返回完整回答，请在 WorkBuddy 查看产物。",job.RemoteId),
            "failed" or "deleted"=>new(DispatchState.Failed,"WorkBuddy 远端状态："+status,job.RemoteId),
            "CREATING" or "idle" or "planning" or "working" or "pending"=>new(DispatchState.Submitted,"WorkBuddy 远端状态："+status,job.RemoteId),
            _=>new(DispatchState.Unknown,"WorkBuddy 返回未识别状态，未推断执行成功。",job.RemoteId)
        };
    }
    private string CloudKey()
    {
        string key=Key(WorkBuddyCredential);
        if(string.IsNullOrWhiteSpace(key))throw new UserError("WorkBuddy 云端尚未授权。无需安装客户端，但需要在任务派发设置中保存官方授权 Access Token。");
        if(key.Length>8192 || key.Any(c=>c<=32 || c>=127))throw new UserError("WorkBuddy Access Token 格式无效，请重新保存官方授权令牌。");
        return key;
    }
    private static bool ValidCloudId(string id)=>id.Length is >0 and <=128 && id.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response,CancellationToken ct,bool submission=false)
    {
        await using var input=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await input.ReadAsync(buffer,ct).ConfigureAwait(false))>0)
        {
            if(output.Length+count>128*1024)
            {
                if(submission)throw new DispatchUncertain("WorkBuddy 响应过大，无法确认接收结果，未自动重发。");
                throw new UserError("远端响应过大，已停止读取。");
            }
            output.Write(buffer,0,count);
        }
        return JsonDocument.Parse(output.ToArray(),new JsonDocumentOptions{MaxDepth=20});
    }
}
