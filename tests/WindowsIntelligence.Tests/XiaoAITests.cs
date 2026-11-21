using System;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WindowsIntelligence;

internal static class XiaoAITests
{
    public static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new XiaoAISettings { Enabled = true, ComputerControl = true, DeviceId = "test-device", DeviceFirmware = "SIMULATED" };
        int executions = 0;
        Task<string> Execute(AssistantInput input,CancellationToken ct) { Interlocked.Increment(ref executions); return Task.FromResult("ok"); }
        AssistantInput Input(string id,string text = "小爱同学，打开电脑上的 VS Code。") =>
            new(AssistantInputKind.XiaoAISpeaker,text,now,"test-device","OH2P",id);
        XiaoAIHandoffReceipt Receipt(string id) => new("test-device",id,DateTimeOffset.UtcNow.AddSeconds(10),true);
        var observer = new XiaoAIInputProvider(settings,Execute,started: now.AddSeconds(-1));
        Need((await observer.ReceiveAsync(Input("weather","今天天气怎么样？"),true,default)).Route == XiaoAIRoute.NativeXiaoAI,"weather must remain native");
        Need((await observer.ReceiveAsync(Input("knowledge","电脑是什么时候发明的？"),true,default)).Route == XiaoAIRoute.NativeXiaoAI,"computer knowledge must remain native");
        Need((await observer.ReceiveAsync(Input("candidate"),true,default)).Code == "awaiting-A-B-C","unverified firmware executed");
        Need((await observer.ReceiveAsync(Input("untrusted"),false,default)).Code == "untrusted-channel","untrusted input accepted");
        Need(executions == 0,"observation executed a command");

        // These flags and receipts simulate a future verified transport; this is not a device test.
        var evidence = new XiaoAIFeasibility(true,true,true,"SIMULATED","test-fixture-only");
        var router = new XiaoAIInputProvider(settings,Execute,evidence,now.AddSeconds(-1));
        Need((await router.ReceiveAsync(Input("no-ack"),true,default)).Code == "missing-native-handoff","global flags bypassed per-event handoff");
        Need((await router.ReceiveAsync(Input("wrong-ack"),true,default,Receipt("other"))).Route == XiaoAIRoute.ObserveWindows,"wrong event acknowledgement accepted");
        Need((await router.ReceiveAsync(Input("execute"),true,default,Receipt("execute"))).Route == XiaoAIRoute.ForwardWindows,"simulated handoff failed");
        Need((await router.ReceiveAsync(Input("execute"),true,default,Receipt("execute"))).Route == XiaoAIRoute.Rejected && executions == 1,"event replay executed twice");
        Need((await router.ReceiveAsync(Input("danger","删除电脑上的文件"),true,default,Receipt("danger"))).Code == "local-confirmation-required","dangerous remote action accepted");
        Need((await router.ReceiveAsync(Input("old") with { Timestamp=now.AddMinutes(-2) },true,default)).Route == XiaoAIRoute.Rejected,"stale event accepted");
        Need((await router.ReceiveAsync(Input("future") with { Timestamp=now.AddMinutes(2) },true,default)).Route == XiaoAIRoute.Rejected,"future event accepted");
        Need((await router.ReceiveAsync(Input("device") with { DeviceId="another" },true,default)).Route == XiaoAIRoute.Rejected,"wrong device accepted");
        settings.DeviceFirmware = "DIFFERENT";
        Need((await router.ReceiveAsync(Input("firmware"),true,default,Receipt("firmware"))).Code == "awaiting-A-B-C","firmware mismatch accepted");
        var gate = new XiaoAIEventGate(now.AddSeconds(-1));
        var accepted = new ConcurrentBag<bool>();
        Parallel.For(0,64,_ => accepted.Add(gate.Accept(Input("concurrent"),"test-device",now)));
        Need(accepted.Count(v => v) == 1,"concurrent replay escaped deduplication");
        for (int i=0;i<1100;i++) gate.Accept(Input("burst-"+i),"test-device",now);
        Need(!gate.Accept(Input("concurrent"),"test-device",now),"burst evicted a valid replay marker");

        const string log = "{\"header\":{\"namespace\":\"SpeechRecognizer\",\"name\":\"RecognizeResult\",\"id\":\"event\"},\"payload\":{\"is_final\":true,\"results\":[{\"text\":\"查询电脑内存占用\"}]}}";
        var device = new XiaoAIDevice("test-device","fixture","OH2P");
        Need(XiaoAIInputProvider.ParseNativeInstruction(log,device,now)?.Text == "查询电脑内存占用","final ASR parser failed");
        Need(XiaoAIInputProvider.ParseNativeInstruction(log.Replace("true","false"),device,now) is null,"partial ASR accepted");
        foreach (string malformed in new[] { "[]","{}","null","{","{\"header\":2}",new string('x',65537) })
            Need(XiaoAIInputProvider.ParseNativeInstruction(malformed,device,now) is null,"malformed log accepted");
        Need(XiaoAIInputProvider.ParseNativeInstruction(log,device with { Model="OTHER" },now) is null,"wrong model parsed");
        string diagnostics = router.Diagnostics.Export();
        Need(!diagnostics.Contains("打开电脑") && !diagnostics.Contains("test-device") && !diagnostics.Contains("test-fixture-only"),"diagnostics exposed content");

        int queries=0, launches=0;
        settings.Applications["测试应用"] = Path.Combine(AppContext.BaseDirectory,"WindowsIntelligence.Tests.exe");
        var actions = new XiaoAIWindowsActions(settings,(_,_) => Task.FromResult("existing AI"),
            (script,_) => { Need(script.Contains("Get-CimInstance Win32_OperatingSystem") && !script.Contains("查询"),"transcript interpolated into shell"); queries++; return Task.FromResult(new PowerShellResult(0,"{\"totalKiB\":100,\"freeKiB\":25}","")); },
            _ => launches++);
        Need((await actions.ExecuteAsync(Input("memory","查询电脑内存占用"),default)).Contains("75"),"fixed memory tool result incorrect");
        await actions.ExecuteAsync(Input("denied","在电脑上执行 PowerShell 删除文件"),default);
        await actions.ExecuteAsync(Input("launch","打开电脑上的测试应用"),default);
        Need(queries == 1 && launches == 1,"restricted tool policy failed");
        var liveReadOnly = new XiaoAIWindowsActions(settings,(_,_) => throw new InvalidOperationException("memory query must not call cloud AI"));
        Need((await liveReadOnly.ExecuteAsync(Input("live-memory","查询电脑内存占用"),default)).StartsWith("当前电脑内存使用率约为百分之"),"real read-only system query failed");
        Console.WriteLine("PASS simulated routing, per-event handoff, replay race, native ASR parsing, restricted tool adapter and live read-only Windows memory query. OH2P hardware: NOT TESTED.");
    }
    private static void Need(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
}
