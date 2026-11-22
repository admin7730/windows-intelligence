using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsIntelligence;

internal enum DispatchTarget { DeepSeekHarness, ChatGptCodex, WorkBuddyCloud }
internal enum DispatchState { Queued, Running, Submitted, Completed, Failed, Cancelled, Interrupted, Unknown }
internal sealed class DispatchWorkspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public override string ToString() => Name + " · " + Directory;
}
internal sealed class DispatchSettings
{
    public List<DispatchWorkspace> Workspaces { get; set; } = [];
    public string HarnessCommand { get; set; } = "";
    public string CodexCommand { get; set; } = "";
    public bool UseDeepSeekCredential { get; set; } = true;
}
internal sealed record DispatchJob(Guid Id, DispatchTarget Target, string Workspace, string Prompt, bool ReadOnly,
    DateTimeOffset CreatedAt, string ResumeSession = "")
{
    public DispatchState State { get; init; } = DispatchState.Queued;
    public string RemoteId { get; init; } = "";
    public string Log { get; init; } = "";
    public string Result { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; } = CreatedAt;
    public string Title => Prompt.Replace('\r',' ').Replace('\n',' ') is var text && text.Length > 38 ? text[..38]+"…" : text;
    public override string ToString() => Title + "\n" + L10n.T(StateName(State));
    internal static string StateName(DispatchState state) => state switch
    {
        DispatchState.Queued=>"排队中",DispatchState.Running=>"正在执行",DispatchState.Submitted=>"远端已接收",DispatchState.Completed=>"已完成",
        DispatchState.Failed=>"执行失败",DispatchState.Cancelled=>"已取消",DispatchState.Interrupted=>"已中断，未自动重做",_=>"结果未确认"
    };
}
internal sealed record DispatchOutcome(DispatchState State, string Result, string RemoteId = "");
internal interface IDispatchRunner
{
    Task<DispatchOutcome> RunAsync(DispatchJob job, Action<string> progress, Action<string> session, CancellationToken ct);
    Task<DispatchOutcome> RefreshAsync(DispatchJob job, CancellationToken ct);
}
internal sealed class DispatchUncertain(string message) : Exception(message);

internal sealed class TaskDispatch : IDisposable
{
    private readonly Settings settings;
    private readonly string file;
    private readonly IDispatchRunner runner;
    private readonly object gate = new();
    private readonly List<DispatchJob> jobs = [];
    private readonly Dictionary<Guid,CancellationTokenSource> active = [];
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource life = new();
    private bool disposed;
    internal event Action? Changed;
    internal string StorageWarning { get; private set; } = "";
    internal TaskDispatch(Settings settings, IDispatchRunner? runner=null, string? directory=null)
    {
        this.settings=settings; this.runner=runner??new ExternalTaskRunner(settings);
        file=Path.Combine(directory??Settings.DirectoryPath,"dispatch-jobs.json");
        if(!settings.SaveTextHistory || !File.Exists(file)) return;
        try
        {
            if(new FileInfo(file).Length>8*1024*1024) throw new IOException();
            var saved=JsonSerializer.Deserialize<List<DispatchJob>>(File.ReadAllText(file))??[];
            foreach(var job in saved.TakeLast(50))
            {
                if(job.Id==Guid.Empty || job.Prompt is null || job.Workspace is null || job.Log is null || job.Result is null || !Enum.IsDefined(job.Target) || !Enum.IsDefined(job.State)) continue;
                jobs.Add(job.State is DispatchState.Queued or DispatchState.Running
                    ? job with {State=DispatchState.Interrupted,Log=Bound(job.Log+"\n应用已退出，未自动重做此任务。",64000)} : job);
            }
        }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException) {StorageWarning="无法读取作业历史，原文件已保留。";}
    }
    internal IReadOnlyList<DispatchJob> Snapshot() {lock(gate)return jobs.OrderByDescending(j=>j.CreatedAt).ToArray();}
    internal Guid Submit(DispatchTarget target,string workspace,string prompt,bool readOnly,string resume="") =>
        Enqueue(target, workspace, prompt, readOnly, resume, null);

    internal Guid SubmitSelected(DispatchIntent intent, DispatchSelection selection) =>
        Enqueue(intent.Target, selection.Project.Directory, intent.Task, selection.ReadOnly, selection.Thread?.Id ?? "", selection.Thread);

    private Guid Enqueue(DispatchTarget target, string workspace, string prompt, bool readOnly, string resume, NativeDispatchThread? selected)
    {
        if(!Enum.IsDefined(target) || string.IsNullOrWhiteSpace(prompt) || prompt.Length>20000) throw new UserError("请填写任务，最多 20000 字。");
        if(!readOnly && !settings.PowerShellEnabled) throw new UserError("电脑自动操作已关闭，请选择只读任务或先启用自动操作。");
        if(target==DispatchTarget.WorkBuddyCloud && readOnly)throw new UserError("WorkBuddy 云端接口没有只读执行参数，请使用账户权限配置，不能把提示词当作只读隔离。");
        workspace=target==DispatchTarget.WorkBuddyCloud?"WorkBuddy Cloud":ValidateWorkspace(workspace);
        if(resume.Length>0 && (target==DispatchTarget.WorkBuddyCloud || !ValidSession(target,resume))) throw new UserError("此任务的续聊标识无效，或目标暂不支持续聊。");
        var job=new DispatchJob(Guid.NewGuid(),target,workspace,prompt.Trim(),readOnly,DateTimeOffset.UtcNow,resume);
        CancellationTokenSource cancellation;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            bool exactSelection = selected is not null && selected.Target == target && selected.Id == resume
                && Path.TrimEndingDirectorySeparator(workspace).Equals(selected.Directory, StringComparison.OrdinalIgnoreCase);
            if (selected is not null && !exactSelection)
                throw new UserError("所选对话与项目不匹配，请重新选择。");
            if(resume.Length>0 && !exactSelection && !jobs.Any(j=>j.Target==target && j.RemoteId==resume && j.Workspace.Equals(workspace,StringComparison.OrdinalIgnoreCase) && j.State is not (DispatchState.Running or DispatchState.Queued)))
                throw new UserError("只能继续作业列表中同一工具、同一工作目录的已结束任务。");
            if (resume.Length > 0 && jobs.Any(j => j.Target == target && active.ContainsKey(j.Id)
                && (j.ResumeSession.Equals(resume, StringComparison.OrdinalIgnoreCase) || j.RemoteId.Equals(resume, StringComparison.OrdinalIgnoreCase))))
                throw new UserError("这个对话已有派发任务，请等它结束后再发送。");
            if(active.Count>=20) throw new UserError("待处理任务已达 20 个，请等待或取消部分任务。");
            while(jobs.Count>=50)
            {
                var old=jobs.FirstOrDefault(j=>!active.ContainsKey(j.Id));
                if(old is null) break; jobs.Remove(old);
            }
            jobs.Add(job); active[job.Id]=cancellation=CancellationTokenSource.CreateLinkedTokenSource(life.Token); Save();
        }
        Changed?.Invoke(); _=Task.Run(()=>RunAsync(job,cancellation)); return job.Id;
    }

    internal async Task<DispatchJob?> WaitForResultAsync(Guid id, CancellationToken cancellation)
    {
        var finished = new TaskCompletionSource<DispatchJob?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            DispatchJob? job;
            lock (gate) job = jobs.Find(j => j.Id == id);
            if (job is null || job.State is not (DispatchState.Running or DispatchState.Queued)) finished.TrySetResult(job);
        }
        Changed += Check;
        try
        {
            Check();
            return await finished.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        finally { Changed -= Check; }
    }
    internal static string ValidateWorkspace(string path)
    {
        if(!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\",StringComparison.Ordinal)) throw new UserError("请选择本机的完整工作目录。");
        string full=Path.GetFullPath(path);
        if(!Directory.Exists(full)) throw new UserError("工作目录不存在。");
        return full;
    }
    internal static bool ValidSession(DispatchTarget target,string id) => target==DispatchTarget.DeepSeekHarness
        ? id.StartsWith("session-",StringComparison.Ordinal) && Guid.TryParse(id[8..],out _)
        : target==DispatchTarget.ChatGptCodex && Guid.TryParse(id,out _);
    private async Task RunAsync(DispatchJob job,CancellationTokenSource cancellation)
    {
        bool entered=false;
        try
        {
            await serial.WaitAsync(cancellation.Token).ConfigureAwait(false); entered=true;
            Update(job.Id,j=>j with {State=DispatchState.Running});
            var outcome=await runner.RunAsync(job,text=>Update(job.Id,j=>j with {Log=Bound(j.Log+"\n"+Redact(text),64000)},persist:false),
                id=>{if(ValidSession(job.Target,id))Update(job.Id,j=>j with {RemoteId=id});},cancellation.Token).ConfigureAwait(false);
            Update(job.Id,j=>j with {State=outcome.State,Result=Bound(Redact(outcome.Result),32000),RemoteId=outcome.RemoteId.Length>0?outcome.RemoteId:j.RemoteId});
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) {Update(job.Id,j=>j with {State=DispatchState.Cancelled,Log=Bound(j.Log+"\n已停止本机执行；不会自动重试。",64000)});}
        catch(DispatchUncertain ex) {Update(job.Id,j=>j with {State=DispatchState.Unknown,Result=Redact(ex.Message)});}
        catch(Exception ex) {Update(job.Id,j=>j with {State=DispatchState.Failed,Result=Bound(Redact(ex.Message),3000)});}
        finally
        {
            lock(gate)active.Remove(job.Id);
            cancellation.Dispose(); if(entered)serial.Release(); Changed?.Invoke();
        }
    }
    internal async Task RefreshAsync(Guid id,CancellationToken ct)
    {
        DispatchJob job; lock(gate)job=jobs.Single(j=>j.Id==id);
        if(job.Target!=DispatchTarget.WorkBuddyCloud || string.IsNullOrEmpty(job.RemoteId)) throw new UserError("只有已接收的 WorkBuddy 云任务可查询远端状态。");
        var result=await runner.RefreshAsync(job,ct);
        Update(id,j=>j with {State=result.State,Result=Redact(result.Result)});
    }
    internal void Cancel(Guid id) {lock(gate)if(active.TryGetValue(id,out var cancellation))cancellation.Cancel();}
    internal void ClearHistory()
    {
        lock(gate)
        {
            jobs.RemoveAll(j=>!active.ContainsKey(j.Id));
            if(File.Exists(file))File.Delete(file);
        }
        Changed?.Invoke();
    }
    private void Update(Guid id,Func<DispatchJob,DispatchJob> update,bool persist=true)
    {
        lock(gate)
        {
            int index=jobs.FindIndex(j=>j.Id==id); if(index<0)return;
            jobs[index]=update(jobs[index]) with {UpdatedAt=DateTimeOffset.UtcNow}; if(persist)Save();
        }
        Changed?.Invoke();
    }
    private void Save()
    {
        if(!settings.SaveTextHistory || StorageWarning.Length>0)return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file+".tmp",JsonSerializer.Serialize(jobs)); File.Move(file+".tmp",file,true); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {StorageWarning="作业历史暂时无法保存，任务仍可在内存中查看。";}
    }
    internal static string Bound(string value,int max)=>value.Length<=max?value:value[..max]+"\n[内容已截断]";
    internal static string Redact(string text)=>Regex.Replace(text,@"(?i)(?:sk-api-[A-Za-z0-9_-]{20,}|sk_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+[^\s'"";,}]+)","[凭据已隐藏]",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;life.Cancel();}}
}
