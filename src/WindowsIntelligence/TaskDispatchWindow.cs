using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsIntelligence;

internal sealed class TaskDispatchWindow : Window
{
    private readonly Settings settings;
    private readonly Interaction interaction;
    private readonly ListBox jobs=new(){BorderThickness=new Thickness(0),Background=Brushes.Transparent};
    private readonly ComboBox target=new(){ItemsSource=new[]{"DeepSeek Harness","ChatGPT / Codex","WorkBuddy Cloud"},SelectedIndex=0,MinHeight=35};
    private readonly ComboBox workspace=new(){IsEditable=true,MinHeight=35,HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly CheckBox readOnly=new(){Content="只读任务",Margin=new Thickness(0,4,0,8)};
    private readonly TextBox prompt=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=104,Padding=new Thickness(12),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock capability=Ui.Text("",12,Ui.Brush("#657084")), notice=Ui.Text("",12,Ui.Brush("#0869E8"));
    private readonly TextBox details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(12),Background=Brushes.White,Foreground=Ui.Brush("#222838"),FontSize=13};
    private readonly Button send,voice;
    private readonly CancellationTokenSource viewLife=new();
    private Guid? selected;
    private string resume="";
    private bool updating,pendingRefresh,recording;
    private readonly object refreshGate=new();

    internal TaskDispatchWindow(Settings settings,Interaction interaction,string draft="")
    {
        this.settings=settings;this.interaction=interaction;
        Title=L10n.T("Windows Intelligence · 任务派发");Width=1040;Height=820;MinWidth=820;MinHeight=620;
        Background=Ui.Brush("#F2F4F8");FontFamily=Ui.Body;UseLayoutRounding=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/WindowsIntelligence;component/SettingsTheme.xaml",UriKind.Relative)});
        foreach(var entry in new[]{("SettingsCanvas","#F2F4F8"),("SettingsSurface","#EFFFFFFF"),("SettingsInk","#222838"),("SettingsMuted","#657084"),("SettingsLine","#1A263858"),("SettingsAccent","#0869E8"),("SettingsSelected","#DCE9FF")})Resources[entry.Item1]=Ui.Brush(entry.Item2);
        var layout=new Grid{Background=Background};layout.ColumnDefinitions.Add(new(){Width=new GridLength(250)});layout.ColumnDefinitions.Add(new());
        var rail=new DockPanel{Background=Ui.Brush("#E8EDF7")};var caption=Ui.Text("作业",20,Ui.Brush("#222838"),true);caption.Margin=new Thickness(18);DockPanel.SetDock(caption,Dock.Top);rail.Children.Add(caption);
        rail.Children.Add(jobs);layout.Children.Add(rail);
        var main=new Grid{Margin=new Thickness(25,20,25,20)};Grid.SetColumn(main,1);layout.Children.Add(main);
        main.RowDefinitions.Add(new(){Height=GridLength.Auto});main.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var form=new StackPanel();main.Children.Add(form);form.Children.Add(Ui.Text("派发任务",24,Ui.Brush("#222838"),true));
        form.Children.Add(Ui.Text("选择执行工具与工作空间。只发送本次任务，不转发整段聊天。",12,Ui.Brush("#657084")));
        var selectors=new Grid{Margin=new Thickness(0,12,0,8)};selectors.ColumnDefinitions.Add(new(){Width=new GridLength(190)});selectors.ColumnDefinitions.Add(new());
        target.Margin=new Thickness(0,0,12,0);selectors.Children.Add(target);Grid.SetColumn(workspace,1);selectors.Children.Add(workspace);form.Children.Add(selectors);
        var commands=new WrapPanel();commands.Children.Add(Button("选择工作目录",ChooseFolder));commands.Children.Add(Button("新任务",()=>{resume="";prompt.Clear();UpdateCapability();}));
        readOnly.IsChecked=!settings.PowerShellEnabled;commands.Children.Add(readOnly);form.Children.Add(commands);
        capability.Margin=new Thickness(0,2,0,9);form.Children.Add(capability);prompt.Text=draft;form.Children.Add(prompt);
        var actions=new WrapPanel{Margin=new Thickness(0,9,0,7)};send=Button("派发",Send);voice=Button("语音派发",()=>_=RecordAsync());actions.Children.Add(send);actions.Children.Add(voice);
        actions.Children.Add(Button("继续此任务",ResumeSelected));actions.Children.Add(Button("取消本机任务",()=>{if(selected is {} id)interaction.Tasks.Cancel(id);}));
        actions.Children.Add(Button("刷新云端状态",()=>_=RefreshCloudAsync()));form.Children.Add(actions);form.Children.Add(notice);
        details.Margin=new Thickness(0,10,0,0);Grid.SetRow(details,1);main.Children.Add(details);Content=layout;
        System.Windows.Automation.AutomationProperties.SetName(prompt,L10n.T("派发任务内容"));
        System.Windows.Automation.AutomationProperties.SetName(target,L10n.T("执行目标"));
        System.Windows.Automation.AutomationProperties.SetName(workspace,L10n.T("工作目录"));
        foreach(var item in settings.Dispatch.Workspaces.Take(30))workspace.Items.Add(item);
        if(workspace.Items.Count>0)workspace.SelectedIndex=0;
        target.SelectionChanged+=(_,_)=>{if(!updating){resume="";UpdateCapability();}};
        workspace.SelectionChanged+=(_,_)=>{if(!updating){resume="";UpdateCapability();}};
        jobs.SelectionChanged+=(_,_)=>{if(updating)return;selected=(jobs.SelectedItem as ListBoxItem)?.Tag as Guid?;UpdateDetails();};
        interaction.Tasks.Changed+=QueueRefresh;
        Closed+=(_,_)=>{interaction.Tasks.Changed-=QueueRefresh;viewLife.Cancel();};
        Loaded+=(_,_)=>L10n.Apply(this);RefreshJobs();UpdateCapability();
    }
    private void UpdateCapability()
    {
        bool cloud=target.SelectedIndex==2;workspace.IsEnabled=!cloud && !recording;
        string text=target.SelectedIndex switch
        {
            0=>DispatchTools.Harness(settings.Dispatch).Length>0?"Harness 已检测到，任务在所选本机目录执行。":"未检测到 Harness，请在设置中指定命令路径。",
            1=>DispatchTools.Codex(settings.Dispatch).Length>0?"使用已登录的 ChatGPT / Codex 本地执行入口，任务在所选目录执行。":"未检测到 Codex，请先安装或配置命令路径。",
            _=>"WorkBuddy 无需本机安装即可使用云端 API，但需单独授权；云沙箱不能直接访问本机工作目录。"
        };
        capability.Text=L10n.T(text)+(resume.Length>0?"\n"+L10n.T("继续会话")+": "+resume:"");
        if(cloud)readOnly.IsChecked=false;
        readOnly.Content=L10n.T("只读任务");
        readOnly.IsEnabled=!cloud && !recording;
        send.IsEnabled=!recording;target.IsEnabled=!recording;
    }
    private string Workspace()=>workspace.SelectedItem is DispatchWorkspace item && workspace.Text==item.ToString()?item.Directory:workspace.Text.Trim();
    private void Send()
    {
        try
        {
            var destination=(DispatchTarget)target.SelectedIndex;string directory=destination==DispatchTarget.WorkBuddyCloud?"WorkBuddy Cloud":TaskDispatch.ValidateWorkspace(Workspace());
            Guid id=interaction.Tasks.Submit(destination,directory,prompt.Text,readOnly.IsChecked==true,resume);
            string savedWarning="";
            if(destination!=DispatchTarget.WorkBuddyCloud && !settings.Dispatch.Workspaces.Any(w=>w.Directory.Equals(directory,StringComparison.OrdinalIgnoreCase)))
            {
                var saved=new DispatchWorkspace{Name=Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),Directory=directory};
                settings.Dispatch.Workspaces.Add(saved);if(settings.Dispatch.Workspaces.Count>30)settings.Dispatch.Workspaces.RemoveAt(0);
                workspace.Items.Add(saved);
                try{settings.Save();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){savedWarning=L10n.T("工作空间暂未保存；已派发的任务继续执行。");}
            }
            selected=id;prompt.Clear();notice.Text=savedWarning.Length>0?savedWarning:L10n.T("任务已排队，关闭窗口后仍会继续。可在作业列表查看进度。");RefreshJobs();
        }
        catch(Exception ex){notice.Text=L10n.T(ex.Message);}
    }
    private async Task RecordAsync()
    {
        try
        {
            if(target.SelectedIndex!=2)TaskDispatch.ValidateWorkspace(Workspace());
            recording=true;voice.IsEnabled=false;UpdateCapability();notice.Text=L10n.T("正在聆听…");
            string text=await interaction.PreviewRecognitionAsync(_=>{},_=>{},()=>Dispatcher.InvokeAsync(()=>notice.Text=L10n.T("正在核对识别结果…")),viewLife.Token);
            if(viewLife.IsCancellationRequested)return;
            if(string.IsNullOrWhiteSpace(text)){notice.Text=L10n.T("没有检测到说话声。");return;}
            prompt.Text=prompt.Text.Trim().Length==0?text:prompt.Text+"\n"+text;Send();
        }
        catch(OperationCanceledException)when(viewLife.IsCancellationRequested){}
        catch(Exception ex){notice.Text=L10n.T(ex.Message);}
        finally{recording=false;voice.IsEnabled=true;UpdateCapability();}
    }
    private void ChooseFolder()
    {
        if(target.SelectedIndex==2){notice.Text=L10n.T("WorkBuddy 云任务不绑定本机目录。");return;}
        var picker=new Microsoft.Win32.OpenFolderDialog{Title=L10n.T("选择工作目录")};
        if(picker.ShowDialog(this)==true){workspace.SelectedIndex=-1;workspace.Text=picker.FolderName;resume="";}
    }
    private void ResumeSelected()
    {
        var job=interaction.Tasks.Snapshot().FirstOrDefault(j=>j.Id==selected);
        if(job is null || !TaskDispatch.ValidSession(job.Target,job.RemoteId)){notice.Text=L10n.T("此任务暂无可继续的本机会话。");return;}
        if(job.State is DispatchState.Running or DispatchState.Queued){notice.Text=L10n.T("请先等待当前任务完成或取消。");return;}
        updating=true;target.SelectedIndex=(int)job.Target;workspace.SelectedIndex=-1;workspace.Text=job.Workspace;readOnly.IsChecked=job.ReadOnly;resume=job.RemoteId;updating=false;UpdateCapability();prompt.Focus();
    }
    private async Task RefreshCloudAsync()
    {
        if(selected is not {} id)return;
        try{await interaction.Tasks.RefreshAsync(id,viewLife.Token);}
        catch(OperationCanceledException)when(viewLife.IsCancellationRequested){}
        catch(Exception ex){notice.Text=L10n.T(ex.Message);}
    }
    private void QueueRefresh()
    {
        lock(refreshGate){if(pendingRefresh || viewLife.IsCancellationRequested)return;pendingRefresh=true;}
        Dispatcher.BeginInvoke(()=>{lock(refreshGate)pendingRefresh=false;if(!viewLife.IsCancellationRequested)RefreshJobs();});
    }
    private void RefreshJobs()
    {
        updating=true;jobs.Items.Clear();
        foreach(var job in interaction.Tasks.Snapshot())
        {
            var content=new StackPanel();var title=Ui.Text("",13,Ui.Brush("#222838"),true);title.Text=TaskDispatch.Redact(job.Title);L10n.Preserve(title);content.Children.Add(title);
            content.Children.Add(Ui.Text(job.Target+" · "+L10n.T(DispatchJob.StateName(job.State)),11,Ui.Brush("#657084")));
            jobs.Items.Add(new ListBoxItem{Content=content,Tag=job.Id,Padding=new Thickness(10),Margin=new Thickness(7,2,7,2),Style=(Style)FindResource("SettingsNavItem")});
        }
        jobs.SelectedItem=jobs.Items.OfType<ListBoxItem>().FirstOrDefault(i=>i.Tag is Guid id && id==selected)??jobs.Items.OfType<ListBoxItem>().FirstOrDefault();
        selected=(jobs.SelectedItem as ListBoxItem)?.Tag as Guid?;updating=false;UpdateDetails();
    }
    private void UpdateDetails()
    {
        var job=interaction.Tasks.Snapshot().FirstOrDefault(j=>j.Id==selected);
        if(job is null){details.Text=L10n.T("尚无作业，选择工具和工作空间后派发。");return;}
        details.Text=L10n.T(DispatchJob.StateName(job.State))+" · "+job.Target+"\n"+job.Workspace+"\n"+(job.RemoteId.Length>0?L10n.T("会话 ID")+": "+job.RemoteId+"\n":"")+
            "\n"+TaskDispatch.Redact(job.Prompt)+"\n\n"+job.Log+"\n\n"+job.Result;
        if(interaction.Tasks.StorageWarning.Length>0)notice.Text=L10n.T(interaction.Tasks.StorageWarning);
    }
    private Button Button(string title,Action action)
    {
        var button=new Button{Content=L10n.T(title),Padding=new Thickness(12,7,12,7),Margin=new Thickness(0,0,7,0),Background=Brushes.White,Foreground=Ui.Brush("#2455A1"),BorderBrush=Ui.Brush("#CAD3E5"),Style=(Style)FindResource("SettingsButton")};
        button.Click+=(_,_)=>action();return button;
    }
}
