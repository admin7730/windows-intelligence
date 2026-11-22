using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsIntelligence;

internal static class DispatchTests
{
    private const string FixtureId = "9e388919-d439-43ea-a84d-2cab4278eea4";
    private static void Need(bool ok,string message){if(!ok)throw new Exception(message);}
    internal static int Fixture(string[] args)
    {
        Console.InputEncoding=Encoding.UTF8;Console.OutputEncoding=new UTF8Encoding(false);
        if (args[0] == "app-server") return CatalogFixture();
        string cwd=Environment.CurrentDirectory;
        if(!Path.GetFileName(cwd).StartsWith("wi-dispatch-test-",StringComparison.Ordinal))return 91;
        string prompt=Console.In.ReadToEnd();
        File.WriteAllText(Path.Combine(cwd,"received.json"),JsonSerializer.Serialize(new{cwd,prompt,args}));
        const string id=FixtureId;
        string secret=Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")??"";
        if(args[0]=="headless")
        {
            Console.WriteLine(JsonSerializer.Serialize(new{type="session",sessionId="session-"+id,cwd}));
            Console.WriteLine("{\"type\":\"thinking\",\"text\":\"PRIVATE_REASONING_MARKER\"}");
            Console.WriteLine(JsonSerializer.Serialize(new{type="final",sessionId="session-"+id,reason=new{kind="completed"},text="fixture answer "+secret}));
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new{type="thread.started",thread_id=id}));
            Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"reasoning\",\"text\":\"PRIVATE_REASONING_MARKER\"}}");
            Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"fixture answer\"}}");
            Console.WriteLine("{\"type\":\"turn.completed\"}");
        }
        return 0;
    }

    private static int CatalogFixture()
    {
        string? cwd = Environment.GetEnvironmentVariable("WI_PICKER_FIXTURE_CWD");
        if (string.IsNullOrEmpty(cwd)) return 92;
        while (Console.ReadLine() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var request = doc.RootElement;
            if (!request.TryGetProperty("id", out var id)) continue;
            object result = request.GetProperty("method").GetString() switch
            {
                "initialize" => new { ok = true },
                "thread/list" when request.GetProperty("params").GetProperty("cursor").ValueKind == JsonValueKind.Null =>
                    new { data = new[] { new { id = Guid.Empty.ToString(), cwd, name = "Other chat", updatedAt = 1760000000 } }, nextCursor = "next-page" },
                _ => new { data = new[] { new { id = FixtureId, cwd, name = "Project testing chat", updatedAt = 1760000100 } }, nextCursor = (string?)null }
            };
            Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
        }
        return 0;
    }

    internal static async Task PickerFlowAsync()
    {
        Need(DispatchIntent.Parse("你去给 DeepSeek 说一声，让它检查测试。") is
            { Target: DispatchTarget.DeepSeekHarness, Task: "检查测试。" }, "Chinese delegation not recognized");
        Need(DispatchIntent.Parse("請你轉給 GPT：檢查設定") is
            { Target: DispatchTarget.ChatGptCodex, Task: "檢查設定" }, "Traditional task text was changed");
        Need(DispatchIntent.Parse("Please ask Codex to list the tests") is
            { Target: DispatchTarget.ChatGptCodex, Task: "list the tests" }, "English delegation not recognized");
        Need(DispatchIntent.Parse("帮我把检查测试这个任务交给 Codex。") is
            { Target: DispatchTarget.ChatGptCodex, Task: "检查测试" }, "task-first delegation not recognized");
        Need(DispatchIntent.Parse("DeepSeek 和 GPT 有什么区别？") is null, "ordinary chat was intercepted");

        var workspace = Directory.CreateTempSubdirectory("wi-dispatch-test-");
        string? previousFixture = Environment.GetEnvironmentVariable("WI_DISPATCH_FIXTURE");
        string? previousCwd = Environment.GetEnvironmentVariable("WI_PICKER_FIXTURE_CWD");
        try
        {
            Environment.SetEnvironmentVariable("WI_DISPATCH_FIXTURE", "1");
            Environment.SetEnvironmentVariable("WI_PICKER_FIXTURE_CWD", workspace.FullName);
            var settings = new Settings { SaveTextHistory = false, Cue = false, AutoSpeak = false, ReduceMotion = true, PowerShellEnabled = false };
            settings.Dispatch.CodexCommand = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            settings.Dispatch.Workspaces.Add(new() { Name = "Fixture project", Directory = workspace.FullName });
            using var overlay = new Overlay(settings);
            using var caption = new Caption();
            using var interaction = new Interaction(settings, Dispatcher.CurrentDispatcher, overlay, caption,
                captureSpeech: (_, _) => Task.FromResult("你去给 Codex 说一声，让它检查测试。"), startIntegrations: false,
                conversations: new Conversations(workspace.FullName, () => false));
            interaction.Start(false);
            Window? popup = null;
            ListBox? chats = null;
            for (int i = 0; i < 200 && chats?.Items.Count != 2; i++)
            {
                await Task.Delay(30);
                popup = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "选择派发位置");
                if (popup is not null) chats = Find<ListBox>(popup, l => AutomationProperties.GetName(l) == "已有对话");
            }
            Need(popup is not null && chats?.Items.Count == 2, "voice flow did not open a picker with both pages of the native catalog");
            Need(interaction.Tasks.Snapshot().Count == 0, "task was sent before confirmation");
            var search = Find<TextBox>(popup!, t => AutomationProperties.GetName(t) == "搜索对话或目录")!;
            search.Text = "Project testing";
            Need(chats!.Items.Count == 1, "chat search did not filter");
            chats.SelectedIndex = 0;
            await Task.Delay(90);
            Snapshot(popup!, "dispatch-picker-fixture.png");
            var confirm = Find<Button>(popup!, b => b.Content as string == "发送任务")!;
            ((IInvokeProvider)new ButtonAutomationPeer(confirm).GetPattern(PatternInterface.Invoke)!).Invoke();
            for (int i = 0; i < 100 && interaction.Tasks.Snapshot().Count == 0; i++) await Task.Delay(30);
            var job = await Done(interaction.Tasks, interaction.Tasks.Snapshot().Single().Id);
            Need(job.State == DispatchState.Completed && job.ResumeSession == FixtureId && job.Prompt == "检查测试。" &&
                job.Workspace == workspace.FullName && job.ReadOnly, "picker sent to wrong session or with wrong task/permissions");
            interaction.Dispose();
            await interaction.ResourceDisposal;
            Console.WriteLine("Voice input → paginated native catalog → search → explicit confirmation → exact pre-existing session passed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WI_DISPATCH_FIXTURE", previousFixture);
            Environment.SetEnvironmentVariable("WI_PICKER_FIXTURE_CWD", previousCwd);
            Need(workspace.FullName.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
                workspace.Name.StartsWith("wi-dispatch-test-", StringComparison.Ordinal), "unsafe fixture directory cleanup");
            Directory.Delete(workspace.FullName, true);
        }
    }

    internal static async Task NativeCatalogAsync()
    {
        var settings = Settings.Load();
        var runner = new ExternalTaskRunner(settings);
        foreach (var target in new[] { DispatchTarget.ChatGptCodex, DispatchTarget.DeepSeekHarness })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            string folder = Directory.CreateTempSubdirectory("wi-dispatch-live-").FullName;
            try
            {
                var original = new DispatchJob(Guid.NewGuid(), target, folder, "Reply exactly WI_DISPATCH_PICKER_NEW. Do not use tools or edit files.", true, DateTimeOffset.UtcNow);
                var first = await runner.RunAsync(original, _ => { }, _ => { }, timeout.Token);
                Need(first.State == DispatchState.Completed && first.Result.Contains("WI_DISPATCH_PICKER_NEW"), target + " new session failed");
                var catalog = await DispatchCatalog.LoadAsync(target, settings, timeout.Token);
                var selected = catalog.Threads.SingleOrDefault(t => t.Id == first.RemoteId);
                Need(selected is not null && selected.Directory.Equals(folder, StringComparison.OrdinalIgnoreCase), target + " new native session not listed in its workspace");
                using var manager = new TaskDispatch(new Settings { SaveTextHistory = false }, runner);
                Guid id = manager.SubmitSelected(new(target, "Reply exactly WI_DISPATCH_PICKER_RESUMED. Do not use tools or edit files."),
                    new(new() { Name = "Picker fixture", Directory = folder }, selected, true));
                var followup = await manager.WaitForResultAsync(id, timeout.Token);
                Need(followup is { State: DispatchState.Completed } && followup.RemoteId == first.RemoteId &&
                    followup.Result.Contains("WI_DISPATCH_PICKER_RESUMED"), target + " did not complete in the selected native session");
                Console.WriteLine($"{target}: {catalog.Threads.Count} native chats found. New chat appeared and continuation used the exact same session ID.");
            }
            finally
            {
                Need(folder.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(folder).StartsWith("wi-dispatch-live-"), "unsafe live fixture cleanup");
                Directory.Delete(folder, true);
            }
        }
    }
    internal static async Task RunAsync()
    {
        var directory=Directory.CreateTempSubdirectory("wi-dispatch-test-");
        string? originalFixture=Environment.GetEnvironmentVariable("WI_DISPATCH_FIXTURE");
        try
        {
            var settings=new Settings{SaveTextHistory=false,Cue=false,AutoSpeak=false,ReduceMotion=true};
            settings.Dispatch.CodexCommand=Path.ChangeExtension(Assembly.GetExecutingAssembly().Location,".exe");
            settings.Dispatch.HarnessCommand=settings.Dispatch.CodexCommand;
            settings.Dispatch.Workspaces.Add(new(){Name="Fixture workspace",Directory=directory.FullName});
            const string secret="opaque-fixture-credential-do-not-display-123456789";
            var runner=new ExternalTaskRunner(settings,credential:_=>secret);
            Environment.SetEnvironmentVariable("WI_DISPATCH_FIXTURE","1");
            string malicious="Do not execute this as shell: $(Write-Output private); ' & \" 你好";
            using var manager=new TaskDispatch(settings,runner,Path.Combine(directory.FullName,"history"));
            Guid id=manager.Submit(DispatchTarget.ChatGptCodex,directory.FullName,malicious,true);
            var job=await Done(manager,id);
            Need(job.State==DispatchState.Completed && job.Result=="fixture answer" && Guid.TryParse(job.RemoteId,out _),"Codex event contract failed");
            using(var received=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,"received.json"))))
            {
                Need(received.RootElement.GetProperty("prompt").GetString()==malicious,"task text was changed or evaluated as shell");
                Need(received.RootElement.GetProperty("cwd").GetString()==directory.FullName,"selected workspace was not used as process cwd");
            }
            Guid continued=manager.Submit(job.Target,job.Workspace,"follow up",true,job.RemoteId);
            Need((await Done(manager,continued)).State==DispatchState.Completed,"resume session contract failed");
            string other=Directory.CreateDirectory(Path.Combine(directory.FullName,"other")).FullName;
            try{manager.Submit(job.Target,other,"wrong workspace",true,job.RemoteId);throw new Exception("cross-workspace resume accepted");}catch(UserError){}
            Guid dsh=manager.Submit(DispatchTarget.DeepSeekHarness,directory.FullName,"Harness fixture",true);
            var harness=await Done(manager,dsh);
            Need(harness.State==DispatchState.Completed && harness.RemoteId.StartsWith("session-") && !harness.Result.Contains(secret) && !harness.Log.Contains(secret),"Harness completion or credential redaction failed");
            Need(manager.Snapshot().All(j=>!j.Log.Contains("PRIVATE_REASONING_MARKER")),"private reasoning was surfaced or saved");
            settings.PowerShellEnabled=false;
            try{manager.Submit(job.Target,directory.FullName,"write",false);throw new Exception("PC-control switch bypassed");}catch(UserError){}
            settings.PowerShellEnabled=true;

            using(var blocking=new TaskDispatch(settings,new BlockRunner(),directory.FullName))
            {
                Guid pending=blocking.Submit(job.Target,directory.FullName,"cancel me",true);
                blocking.Cancel(pending);Need((await Done(blocking,pending)).State==DispatchState.Cancelled,"queued task cancellation failed");
            }
            settings.SaveTextHistory=true;
            string savedDir=Path.Combine(directory.FullName,"restart");Directory.CreateDirectory(savedDir);
            File.WriteAllText(Path.Combine(savedDir,"dispatch-jobs.json"),JsonSerializer.Serialize(new[]{job with{State=DispatchState.Running}}));
            using(var restarted=new TaskDispatch(settings,new BlockRunner(),savedDir))Need(restarted.Snapshot().Single().State==DispatchState.Interrupted,"restart retried an incomplete task");
            settings.SaveTextHistory=false;

            using var cloudHttp=new HttpClient(new CloudFixture());
            var cloud=new ExternalTaskRunner(settings,cloudHttp,_=>"oauth-fixture");
            var cloudJob=new DispatchJob(Guid.NewGuid(),DispatchTarget.WorkBuddyCloud,"WorkBuddy Cloud","Cloud test",false,DateTimeOffset.UtcNow);
            var accepted=await cloud.RunAsync(cloudJob,_=>{},_=>{},default);
            Need(accepted.State==DispatchState.Submitted && accepted.RemoteId=="cloud-job-1" && !accepted.Result.Contains("private-cloud-token"),"cloud receipt was misreported as completion or leaked token");
            var refreshed=await cloud.RefreshAsync(cloudJob with{RemoteId=accepted.RemoteId},default);
            Need(refreshed.State==DispatchState.Completed && refreshed.Result.Contains("不返回完整回答"),"cloud completion status scope was misrepresented");
            try{manager.Submit(DispatchTarget.WorkBuddyCloud,"anything","read-only",true);throw new Exception("cloud accepted an unsupported readonly promise");}catch(UserError){}
            using(var oversized=new HttpClient(new OversizedCloudFixture()))
            {
                try{await new ExternalTaskRunner(settings,oversized,_=>"oauth-fixture").RunAsync(cloudJob,_=>{},_=>{},default);throw new Exception("ambiguous cloud receipt reported success");}
                catch(DispatchUncertain){}
            }
            try{await new ExternalTaskRunner(settings,cloudHttp,_=>"private\r\ninvalid").RunAsync(cloudJob,_=>{},_=>{},default);throw new Exception("invalid credential accepted");}
            catch(UserError ex){Need(!ex.Message.Contains("private"),"invalid credential was displayed");}

            using var overlay=new Overlay(settings);using var caption=new Caption();
            using var interaction=new Interaction(settings,Dispatcher.CurrentDispatcher,overlay,caption,captureSpeech:(_,_)=>Task.FromResult(""),startIntegrations:false);
            var window=new TaskDispatchWindow(settings,interaction,"Fixture task");window.Show();await Task.Delay(90);
            Need(Find<Button>(window,b=>b.Content as string=="派发") is {IsVisible:true},"dispatch action missing");
            Snapshot(window,"dispatch-zh.png");window.Close();
            var settingsWindow=new SettingsWindow(settings,interaction,overlay,0);settingsWindow.Show();await Task.Delay(70);
            Find<ListBoxItem>(settingsWindow,i=>i.Tag is int page && page==14)!.IsSelected=true;await Task.Delay(70);
            Need(Find<Button>(settingsWindow,b=>b.Content as string=="打开任务派发") is not null,"dispatch settings missing");
            Snapshot(settingsWindow,"dispatch-settings-zh.png");settingsWindow.Close();
            try
            {
                L10n.Language=AppLanguage.English;settings.Language=AppLanguage.English;
                var english=new TaskDispatchWindow(settings,interaction,"A task in English");english.Show();await Task.Delay(70);Snapshot(english,"dispatch-en.png");english.Close();
            }
            finally{L10n.Language=AppLanguage.SimplifiedChinese;settings.Language=AppLanguage.SimplifiedChinese;}
            interaction.Dispose();await interaction.ResourceDisposal;
            Console.WriteLine("Workspace routing, stdin boundaries, sessions, cancellation, restart, permission switch, cloud receipt semantics, redaction and UI passed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WI_DISPATCH_FIXTURE",originalFixture);
            Need(directory.FullName.StartsWith(Path.GetTempPath(),StringComparison.OrdinalIgnoreCase) && directory.Name.StartsWith("wi-dispatch-test-"),"unsafe fixture cleanup");
            Directory.Delete(directory.FullName,true);
        }
    }
    internal static async Task LiveAsync(DispatchTarget target)
    {
        string directory=Directory.CreateTempSubdirectory("wi-dispatch-live-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory,"README.txt"),"Read-only Windows Intelligence dispatch verification.");
            var settings=Settings.Load();
            var runner=new ExternalTaskRunner(settings);
            var job=new DispatchJob(Guid.NewGuid(),target,directory,"Reply with exactly WI_DISPATCH_LIVE_OK. Do not use tools or modify files.",true,DateTimeOffset.UtcNow);
            using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result=await runner.RunAsync(job,_=>{},_=>{},ct.Token);
            Need(result.State==DispatchState.Completed && result.Result.Contains("WI_DISPATCH_LIVE_OK"),"live native dispatch was not completed");
            Console.WriteLine("Live "+target+" dispatch completed in the selected temporary workspace. Session: "+result.RemoteId);
        }
        finally
        {
            Need(directory.StartsWith(Path.GetTempPath(),StringComparison.OrdinalIgnoreCase) && Path.GetFileName(directory).StartsWith("wi-dispatch-live-"),"unsafe live fixture cleanup");
            Directory.Delete(directory,true);
        }
    }
    private static async Task<DispatchJob> Done(TaskDispatch manager,Guid id)
    {
        for(int i=0;i<300;i++)
        {
            var job=manager.Snapshot().Single(j=>j.Id==id);
            if(job.State is not (DispatchState.Queued or DispatchState.Running))return job;
            await Task.Delay(30);
        }
        throw new Exception("dispatch fixture timed out");
    }
    private sealed class BlockRunner:IDispatchRunner
    {
        public async Task<DispatchOutcome> RunAsync(DispatchJob job,Action<string> log,Action<string> session,CancellationToken ct){await Task.Delay(Timeout.Infinite,ct);return new(DispatchState.Completed,"");}
        public Task<DispatchOutcome> RefreshAsync(DispatchJob job,CancellationToken ct)=>throw new NotSupportedException();
    }
    private sealed class CloudFixture:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Need(request.RequestUri!.Host=="www.workbuddy.cn" && request.Headers.Authorization?.Parameter=="oauth-fixture","cloud auth escaped official host");
            string response=request.Method==HttpMethod.Post?"{\"task_id\":\"cloud-job-1\",\"status\":\"working\",\"token\":\"private-cloud-token\"}":"{\"task_id\":\"cloud-job-1\",\"status\":\"completed\"}";
            return Task.FromResult(new HttpResponseMessage(request.Method==HttpMethod.Post?HttpStatusCode.Created:HttpStatusCode.OK){Content=new StringContent(response,Encoding.UTF8,"application/json")});
        }
    }
    private sealed class OversizedCloudFixture:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Created){Content=new StringContent(new string('x',129*1024))});
    }
    private static T? Find<T>(DependencyObject parent,Func<T,bool> match)where T:DependencyObject
    {
        if(parent is T item && match(item))return item;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(Find(VisualTreeHelper.GetChild(parent,i),match) is {} found)return found;
        return null;
    }
    private static void Snapshot(Window window,string name)
    {
        var visual=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(AppContext.BaseDirectory,name));png.Save(file);
    }
}
