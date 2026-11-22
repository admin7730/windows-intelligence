using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsIntelligence;

internal enum AppLanguage { SimplifiedChinese, TraditionalChinese, English }
internal static class L10n
{
    private static AppLanguage language;
    internal static event Action? Changed;
    internal static AppLanguage Language { get => language; set { if(language==value) return; language=value; Changed?.Invoke(); } }
    internal static string F(string chinese, string english, params object[] values) => Language == AppLanguage.English
        ? string.Format(english,values) : T(string.Format(chinese,values));
    private static readonly Dictionary<string,string> English = Load();
    private static readonly Dictionary<string,string> Sources = English.Where(p=>p.Key.Any(c=>c is >= '\u4e00' and <= '\u9fff')).GroupBy(p=>p.Value).ToDictionary(g=>g.Key,g=>g.First().Key);
    private static readonly DependencyProperty PreserveProperty = DependencyProperty.RegisterAttached("Preserve",typeof(bool),typeof(L10n),new PropertyMetadata(false));
    internal static void Preserve(DependencyObject value) => value.SetValue(PreserveProperty,true);
    private static Dictionary<string,string> Load()
    {
        using var stream=typeof(L10n).Assembly.GetManifestResourceStream("WindowsIntelligence.Strings.en.json") ?? throw new InvalidOperationException("Language resources missing.");
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;
    }
    internal static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (Sources.TryGetValue(text,out var source)) text=source;
        if (Language==AppLanguage.TraditionalChinese) return MapChinese(text,true);
        if (Language==AppLanguage.SimplifiedChinese) return text;
        if (English.TryGetValue(text,out var translated)) return translated;
        foreach (var split in new[] { " · ", "：", "\n" })
        {
            if (!text.Contains(split,StringComparison.Ordinal)) continue;
            return string.Join(split=="："?": ":split,text.Split(split).Select(part=>English.TryGetValue(part,out var value)?value:part));
        }
        return text;
    }
    internal static string MapChinese(string text,bool traditional)
    {
        if (text.Length==0) return text;
        var mapped=new char[text.Length*2+8];
        int count=LCMapStringEx("zh-CN",traditional?0x04000000u:0x02000000u,text,text.Length,mapped,mapped.Length,0,0,0);
        return count>0?new string(mapped,0,count):text;
    }
    internal static string ReplyPreference(AppLanguage language) => language switch
    {
        AppLanguage.TraditionalChinese=>"除非用戶在本次要求其他語言，請使用繁體中文回答。",
        AppLanguage.English=>"Respond in English unless the user explicitly requests a different language in this message.",
        _=>"除非用户在本次要求其他语言，请使用简体中文回答。"
    };
    internal static void Apply(DependencyObject root)
    {
        if ((bool)root.GetValue(PreserveProperty)) return;
        if(root is FrameworkElement accessible)
        {
            string name=System.Windows.Automation.AutomationProperties.GetName(accessible);
            if(name.Length>0) System.Windows.Automation.AutomationProperties.SetName(accessible,T(MapChinese(name,false)));
        }
        if (root is TextBox or PasswordBox or MarkdownReplyView) return;
        if(root is Window window) window.Title=T(MapChinese(window.Title,false));
        if(root is TextBlock label) label.Text=T(MapChinese(label.Text,false));
        if(root is Button button && button.Content is string content) button.Content=T(MapChinese(content,false));
        if(root is FrameworkElement element && element.ToolTip is string tip) element.ToolTip=T(MapChinese(tip,false));
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) Apply(VisualTreeHelper.GetChild(root,i));
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern int LCMapStringEx(string locale,uint flags,string source,int sourceLength,[Out] char[] destination,int destinationLength,nint version,nint reserved,nint sort);
}
