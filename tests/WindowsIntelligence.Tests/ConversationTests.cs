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

internal static class ConversationTests
{
    private static void Need(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("wi-conversations-test-");
        try
        {
            var now = DateTimeOffset.UtcNow;
            string legacy = Path.Combine(directory.FullName, "history.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(new[] { new AiTurn("以前的问题", "以前的答案") }));
            string original = File.ReadAllText(legacy);
            var store = new Conversations(directory.FullName, () => true, () => now);
            Need(store.Items.Count == 1 && store.Active is null && File.ReadAllText(legacy) == original, "legacy migration lost data or automatically resumed context");
            var first = store.Begin(15); store.Add(first, new("alpha", "one"));
            now += TimeSpan.FromMinutes(14);
            Need(ReferenceEquals(store.Begin(15),first), "continuous conversation did not resume within 15 minutes");
            now += TimeSpan.FromMinutes(15);
            var second = store.Begin(15);
            Need(second.Id != first.Id && second.Turns.Count == 0, "idle context leaked into a new conversation");
            store.Add(second,new("beta","two",new([1,2,3],"image/png")));
            Need(store.Begin(15,true).Id != second.Id, "manual new conversation was ignored");
            Need(store.Select(first.Id) && store.Begin(15).Turns.Single().Text=="alpha", "history resume mixed conversations");
            var reloaded = new Conversations(directory.FullName,()=>true,()=>now);
            Need(reloaded.Active is null && reloaded.Items.Any(c=>c.Id==second.Id && c.Turns.Single().Image is null), "restart resumed context or persisted image bytes");
            for(int i=0;i<310;i++) reloaded.Begin(15,true);
            Need(reloaded.Items.Any(c=>c.Id==first.Id),"empty conversation openings evicted completed history");
            reloaded.Delete(second.Id);
            Need(!new Conversations(directory.FullName,()=>true).Items.Any(c=>c.Id==second.Id), "deleted conversation reappeared");
            var ephemeral = new Conversations(Path.Combine(directory.FullName,"ephemeral"),()=>false);
            ephemeral.Add(ephemeral.Begin(15),new("private","private"));
            Need(!Directory.Exists(Path.Combine(directory.FullName,"ephemeral")),"history-off mode persisted private text");

            var settings = new Settings { AutoSpeak=false, Cue=false, SaveTextHistory=false, ReduceMotion=true };
            Need(CommandMemory.Match("查询 deep SEEK 用量！",settings.Memory)?.Action==RememberedAction.DeepSeekUsage,"normalized command matching failed");
            Need(CommandMemory.Match("不要查询 DeepSeek 用量",settings.Memory) is null,"negation incorrectly triggered an account query");
            Need(CommandMemory.Match("查詢 DeepSeek 餘額",settings.Memory)?.Action==RememberedAction.DeepSeekUsage &&
                CommandMemory.Match("Check MiniMax usage",settings.Memory)?.Action==RememberedAction.MiniMaxUsage,"traditional or English command aliases failed");
            settings.Memory.Personal="回答尽量简短";
            var built = MultimodalRequestBuilder.Build(Guid.NewGuid(),settings,"hello",null,[]);
            Need(built.Memory==settings.Memory.Personal,"personal memory not included in request");
            var duplicate=new MemorySettings(); duplicate.Commands.Add(new() {Phrases="查询deepseek用量",Action=RememberedAction.DeepSeekUsage});
            try { CommandMemory.Validate(duplicate); throw new Exception("duplicate rule accepted"); } catch(UserError) {}

            var handler=new UsageFixture(); using var http=new HttpClient(handler);
            var usage=new ProviderUsage(http,id=>id==ProviderUsage.MiniMaxPlanCredential?"plan-fixture":"deepseek-fixture");
            string balance=await usage.QueryAsync(RememberedAction.DeepSeekUsage,settings,default);
            Need(balance.Contains("110.00") && balance.Contains("不包含历史") && handler.Path=="/user/balance", "official balance query or honest usage scope failed");
            int count=handler.Calls;
            string payg=await usage.QueryAsync(RememberedAction.MiniMaxUsage,settings,default);
            Need(handler.Calls==count && payg.Contains("不能仅凭语音 Key"),"pay-as-you-go key was sent to a subscription API or a fake balance was returned");
            settings.Memory.MiniMaxSubscription=true;
            string quota=await usage.QueryAsync(RememberedAction.MiniMaxUsage,settings,default);
            Need(quota.Contains("remaining_count") && !quota.Contains("private-response-secret") && handler.Key=="plan-fixture","quota query leaked private fields or used TTS key");
            handler.Failure=true;
            try { await usage.QueryAsync(RememberedAction.DeepSeekUsage,settings,default); throw new Exception("authentication failure accepted"); } catch(UserError) {}
            handler.Failure=false;

            using var overlay=new Overlay(settings); using var caption=new Caption();
            var interactionStore=new Conversations(Path.Combine(directory.FullName,"interaction"),()=>false,()=>now);
            using var aiHttp=new HttpClient(new NoAi());
            using var interaction=new Interaction(settings,Dispatcher.CurrentDispatcher,overlay,caption,new AiClient(aiHttp),(_,_)=>Task.FromResult(""),startIntegrations:false,conversations:interactionStore,usage:usage);
            interaction.Start(false,typed:true,silent:true);
            var captionWindow=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="Windows Intelligence · 助手");
            await Task.Delay(60);
            Find<TextBox>(captionWindow,b=>true)!.Text="查询 DeepSeek 用量";
            var send=Find<Button>(captionWindow,b=>System.Windows.Automation.AutomationProperties.GetName(b)=="发送问题");
            Need(send is not null,"native query send entry missing");
            send!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for(int i=0;i<100 && !interaction.HistoryText.Contains("110.00");i++) await Task.Delay(30);
            Need(interaction.HistoryText.Contains("110.00"),"matched native query did not use the existing response/history flow");
            Guid previous=interactionStore.Active!.Id;
            interaction.NewConversation();
            Need(interactionStore.Active!.Id!=previous && interactionStore.Active.Turns.Count==0,"new UI conversation retained old context");
            interaction.ResumeConversation(previous);
            Need(interactionStore.Active.Id==previous && interactionStore.Active.Turns.Count==1,"historical resume failed");
            var historyWindow=new ConversationWindow(interaction); historyWindow.Show(); await Task.Delay(80);
            Need(Find<Button>(historyWindow,b=>b.Content as string=="继续此对话") is {IsVisible:true},"history browser is not usable");
            Snapshot(historyWindow,"conversations-preview.png"); historyWindow.Close();
            var settingsWindow=new SettingsWindow(settings,interaction,overlay,0); settingsWindow.Show(); await Task.Delay(80);
            Find<ListBoxItem>(settingsWindow,i=>i.Tag is int id && id==13)!.IsSelected=true; await Task.Delay(80);
            Need(Find<Button>(settingsWindow,b=>b.Content as string=="添加指令记忆") is {IsVisible:true},"memory editor missing");
            Snapshot(settingsWindow,"memory-settings-preview.png"); settingsWindow.Close();
            try
            {
                foreach(var language in new[]{AppLanguage.TraditionalChinese,AppLanguage.English})
                {
                    settings.Language=language; L10n.Language=language;
                    Need(L10n.T("语音识别")== (language==AppLanguage.English?"Speech recognition":"語音識別"),"language resource or traditional conversion failed");
                    var localized=new SettingsWindow(settings,interaction,overlay,0); localized.Show(); await Task.Delay(90);
                    Need(Find<Button>(localized,b=>b.Content as string==L10n.T("保存设置")) is not null,"localized save control missing");
                    Snapshot(localized,language==AppLanguage.English?"settings-en.png":"settings-zh-Hant.png");
                    Find<ListBoxItem>(localized,i=>i.Tag is int id && id==13)!.IsSelected=true; await Task.Delay(70);
                    Need(Find<Button>(localized,b=>b.Content as string==L10n.T("添加指令记忆")) is not null,"localized memory editor missing");
                    Snapshot(localized,language==AppLanguage.English?"memory-en.png":"memory-zh-Hant.png");
                    localized.Close();
                }
            }
            finally { settings.Language=AppLanguage.SimplifiedChinese; L10n.Language=AppLanguage.SimplifiedChinese; }
            interaction.Dispose(); await interaction.ResourceDisposal;
            Console.WriteLine("Conversation boundaries, migration, restart, manual resume, memory matching, native billing routes, credential boundaries and history UI passed.");
        }
        finally
        {
            string resolved=Path.GetFullPath(directory.FullName);
            Need(resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) && directory.Name.StartsWith("wi-conversations-test-"),"unsafe fixture cleanup");
            Directory.Delete(resolved,true);
        }
    }

    private sealed class UsageFixture : HttpMessageHandler
    {
        internal int Calls; internal string Path="",Key=""; internal bool Failure;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++; Path=request.RequestUri!.AbsolutePath; Key=request.Headers.Authorization!.Parameter!;
            Need(request.Method==HttpMethod.Get && request.RequestUri.Scheme=="https","billing query was not a read-only HTTPS call");
            string json=Path=="/user/balance"?"{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"110.00\",\"granted_balance\":\"10.00\",\"topped_up_balance\":\"100.00\"}]}":"{\"base_resp\":{\"status_code\":0},\"remaining_count\":42,\"secret\":\"private-response-secret\"}";
            return Task.FromResult(new HttpResponseMessage(Failure?HttpStatusCode.Unauthorized:HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
    private sealed class NoAi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new Exception("Native account query unexpectedly called an AI service");
    }
    private static T? Find<T>(DependencyObject parent,Func<T,bool> match) where T:DependencyObject
    {
        if(parent is T value && match(value)) return value;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) if(Find(VisualTreeHelper.GetChild(parent,i),match) is {} found) return found;
        return null;
    }
    private static void Snapshot(Window window,string name)
    {
        var visual=(FrameworkElement)window.Content; var image=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32); image.Render(visual);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file=File.Create(Path.Combine(AppContext.BaseDirectory,name)); encoder.Save(file);
    }
}
