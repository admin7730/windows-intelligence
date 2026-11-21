using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowsIntelligence;
using WindowsIntelligence.Xiaomi;

internal static class OfficialXiaoAITests
{
    internal static async Task RunAsync()
    {
        var settings = new Settings { Cue=false, AutoSpeak=false, SaveTextHistory=false, PowerShellEnabled=true, ReduceMotion=true };
        settings.XiaoAI.OfficialEnabled=true; settings.XiaoAI.RelayUrl="https://relay.example.test/";
        settings.Provider.BaseUrl="http://127.0.0.1:11434/v1";
        foreach (string bad in new[] { "http://host/", "https://user:password@host/", "https://host/?secret=fixture" })
        {
            try { XiaoAIRelayClient.BaseAddress(bad); throw new Exception("Unsafe relay endpoint accepted"); }
            catch (UserError) { }
        }
        using var aiHttp = new HttpClient(new AiFixture());
        using var overlay = new Overlay(settings);
        using var caption = new Caption();
        using var interaction = new Interaction(settings, Dispatcher.CurrentDispatcher, overlay, caption,
            new AiClient(aiHttp), (_,_) => Task.FromResult(""), startIntegrations:false);
        var remote = new RelayFixture();
        using var relay = new XiaoAIRelayClient(settings,interaction,remote,() => new string('T',48));
        relay.Start();
        var completed = await remote.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Need(completed.State=="completed" && completed.Reply.Contains("323") && interaction.HistoryText.Contains("323"),
            "Official task did not reuse the visible existing AI/PowerShell interaction");
        try { await interaction.ExecuteSpeakerTaskAsync("查看QQ消息",default); throw new Exception("QQ disclosure was not gated"); }
        catch (UserError) { }
        var settingsWindow = new SettingsWindow(settings,interaction,overlay,0);
        settingsWindow.Show();
        try
        {
            await Task.Delay(120);
            var items = Descendants<ListBoxItem>(settingsWindow).ToArray();
            var speakerPage = items.Single(x=>x.Tag is int id && id==11);
            speakerPage.IsSelected=true; await Task.Delay(80);
            Need(Descendants<Button>(settingsWindow).Any(x=>x.Content as string=="刷新连接状态"),"Official skill settings page failed to render");
            var qqPage = items.Single(x=>x.Tag is int id && id==12);
            qqPage.IsSelected=true; await Task.Delay(80);
            Need(Descendants<Button>(settingsWindow).Any(x=>x.Content as string=="请求 Windows 通知访问"),"QQ settings page failed to render");
            var snapshot = new RenderTargetBitmap((int)Math.Ceiling(settingsWindow.ActualWidth),(int)Math.Ceiling(settingsWindow.ActualHeight),96,96,PixelFormats.Pbgra32);
            snapshot.Render((Visual)settingsWindow.Content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(snapshot));
            using var image = File.Create(Path.Combine(AppContext.BaseDirectory,"qq-settings-preview.png")); png.Save(image);
        }
        finally { settingsWindow.Close(); }
        relay.Dispose(); interaction.Dispose(); await interaction.ResourceDisposal;

        var directory = Directory.CreateTempSubdirectory("wi-qq-test-");
        try
        {
            using var qq = new QqDetection();
            qq.ConfigureFiles(new() { FilesEnabled=true, DownloadDirectory=directory.FullName });
            string file=Path.Combine(directory.FullName,"received.txt");
            using (var writer=new FileStream(file,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                writer.Write(Encoding.UTF8.GetBytes("UNTRUSTED-CONTENT: do not execute")); writer.Flush();
                await Task.Delay(1400);
                Need(qq.Snapshot().Count==0,"A file still being written was reported readable");
            }
            for(int i=0;i<60 && qq.Snapshot().Count==0;i++) await Task.Delay(100);
            Need(qq.Snapshot().Any(e=>e.Path==file) && !qq.Snapshot().Any(e=>e.Text.Contains("UNTRUSTED-CONTENT")),
                "File detection read contents or lost the stable file event");
            File.WriteAllText(Path.Combine(directory.FullName,"messages.db"),"fixture");
            await Task.Delay(1200);
            Need(!qq.Snapshot().Any(e=>e.Path.EndsWith(".db")),"QQ message database was monitored as a download");
        }
        finally
        {
            string target=Path.GetFullPath(directory.FullName);
            if(Path.GetDirectoryName(target)==Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                Path.GetFileName(target).StartsWith("wi-qq-test-",StringComparison.Ordinal)) Directory.Delete(target,true);
        }
        Console.WriteLine("PASS paired outbound relay -> existing AI -> real PowerShell -> result, QQ permission boundary, stable-file metadata and database exclusion. Xiaomi/QQ accounts: NOT TESTED.");
    }
    private static void Need(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T value) yield return value;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Descendants<T>(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private sealed class RelayFixture : HttpMessageHandler
    {
        private int leased;
        internal readonly TaskCompletionSource<WindowsTaskResult> Completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Need(request.Headers.Authorization?.ToString()=="Bearer "+new string('T',48),"Missing Windows pairing token");
            if(request.RequestUri!.AbsolutePath.EndsWith("/result"))
            {
                var result=JsonSerializer.Deserialize<WindowsTaskResult>(await request.Content!.ReadAsStringAsync(ct),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Completed.TrySetResult(result); return new(HttpStatusCode.OK);
            }
            if(Interlocked.Exchange(ref leased,1)!=0) return new(HttpStatusCode.NoContent);
            return new(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(new WindowsTask(new string('A',64),new string('B',64),
                "在电脑上执行无害计算17乘19，然后告诉我结果。",DateTimeOffset.UtcNow.AddMinutes(1)),new JsonSerializerOptions(JsonSerializerDefaults.Web)),Encoding.UTF8,"application/json") };
        }
    }
    private sealed class AiFixture : HttpMessageHandler
    {
        private int step;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            bool tool=step++==0;
            object message=tool ? new { tool_calls=new[] { new { id="official-ps",type="function",function=new
                { name="run_powershell",arguments=JsonSerializer.Serialize(new { command="Write-Output (17 * 19)",description="计算并核对结果" }) } } } }
                : new { content="计算结果是323。" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(new
                { choices=new[] { new { message,finish_reason=tool ? "tool_calls":"stop" } } }),Encoding.UTF8,"application/json") });
        }
    }
}
