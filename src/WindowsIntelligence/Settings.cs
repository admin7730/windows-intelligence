using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsIntelligence;

internal enum CaptureTarget { CurrentMonitor, PrimaryMonitor, AllMonitors, ActiveWindow, SelectRegion }
internal enum TtsMode { Disabled, Local, Custom, ElevenLabs, MiniMax }
internal enum Quality { Low, Balanced, High, Ultra }

internal sealed class ProviderSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "DeepSeek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string TextModel { get; set; } = "deepseek-flash";
    public string VisionModel { get; set; } = "deepseek-flash";
    public bool SupportsText { get; set; } = true;
    public bool SupportsVision { get; set; } = true;
    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsTools { get; set; } = true;
    public bool Streaming { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 90;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 1536;
    public string SystemPrompt { get; set; } = "你是 Windows 桌面助手。按用户使用的语言回答。给出简洁、准确、可执行的说明。";
}

internal sealed class TtsProviderSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "OpenAI-compatible";
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Format { get; set; } = "wav";
    public int SampleRate { get; set; }
}

internal sealed class Settings
{
    public List<ProviderSettings> Providers { get; set; } = [new()];
    public Guid ActiveProvider { get; set; }
    [JsonIgnore] public ProviderSettings Provider => Providers.Find(p => p.Id == ActiveProvider) ?? Providers[0];
    public List<TtsProviderSettings> TtsProviders { get; set; } = [new()];
    public Guid ActiveTtsProvider { get; set; }
    [JsonIgnore] public TtsProviderSettings TtsProvider => TtsProviders.Find(p => p.Id == ActiveTtsProvider) ?? TtsProviders[0];
    public TtsMode Tts { get; set; } = TtsMode.MiniMax;
    public string MiniMaxVoice { get; set; } = "male-qn-qingse";
    public double TtsSpeed { get; set; } = 1;
    public int LocalVoice { get; set; } = 43;
    public bool AutoSpeak { get; set; } = true;
    public bool Cue { get; set; } = true;
    public string ElevenLabsVoice { get; set; } = "JBFqnCBsd6RMkjVDRZzb";
    public string ElevenLabsModel { get; set; } = "eleven_flash_v2_5";
    public CaptureTarget Target { get; set; }
    public int VoiceWait { get; set; } = 2500;
    public int Silence { get; set; } = 1200;
    public bool Strict { get; set; }
    public bool MicrophoneEnabled { get; set; } = true;
    public AssistantShortcut? Shortcut { get; set; }
    public string MicrophoneDeviceId { get; set; } = "";
    public bool AccurateSpeech { get; set; } = true;
    public bool PowerShellEnabled { get; set; } = true;
    public bool AutoSend { get; set; } = true;
    public bool Preview { get; set; } = true;
    public string? ScreenshotConsent { get; set; }
    public bool ImageHistory { get; set; }
    public bool SaveTextHistory { get; set; } = true;
    public string ImageFormat { get; set; } = "PNG";
    public string ImageDetail { get; set; } = "original";
    public int JpegQuality { get; set; } = 92;
    public int MaxImageSide { get; set; } = 7680;
    public string ScreenshotPrompt { get; set; } = "请分析这张屏幕截图。先识别画面的主要内容，再解释重要信息。根据截图中的错误、代码、图表、软件或题目给出针对性帮助。不要编造截图中不存在的信息。";
    public bool Edge { get; set; } = true;
    public bool Particles { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public bool Tray { get; set; } = true;
    public int OrbSize { get; set; } = 160;
    public double Glass { get; set; } = 0.94;
    public double Blur { get; set; } = 0.6;
    public double Glow { get; set; } = 0.87;
    public double Palette { get; set; }
    public double AnimationSpeed { get; set; } = 1;
    public double Sensitivity { get; set; } = 1;
    public int OrbPosition { get; set; } = 2;
    public int ParticleCount { get; set; } = 70;
    public Quality Quality { get; set; } = Quality.Balanced;
    public bool Adaptive { get; set; } = true;
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsIntelligence");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (settings.Providers.Count == 0) settings.Providers.Add(new());
            if (settings.TtsProviders.Count == 0) settings.TtsProviders.Add(new());
            return settings;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }

    [JsonIgnore] public string DestinationKey => $"{Provider.Id:N}|{AiEndpoint.Create(Provider.BaseUrl, "chat/completions").AbsoluteUri}";
}

internal static class SecretStore
{
    public static string Get(Guid id)
    {
        if (!CredRead($"WindowsIntelligence/{id:N}", 1, 0, out var pointer)) return "";
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(value.Blob, (int)value.BlobSize / 2) ?? "";
        }
        finally { CredFree(pointer); }
    }

    public static void Set(Guid id, string secret)
    {
        string name = $"WindowsIntelligence/{id:N}";
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!CredDelete(name, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new System.ComponentModel.Win32Exception();
            return;
        }
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var value = new Credential { Type = 1, Target = name, Blob = blob, BlobSize = (uint)Encoding.Unicode.GetByteCount(secret), Persist = 2, UserName = Environment.UserName };
            if (!CredWrite(ref value, 0)) throw new System.ComponentModel.Win32Exception();
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string Alias, UserName;
    }

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out nint value);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref Credential value, uint flags);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32")] private static extern void CredFree(nint value);
}

internal static class AiEndpoint
{
    public static Uri Create(string baseUrl, string path = "")
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var root) || (root.Scheme != "https" && !(root.Scheme == "http" && root.IsLoopback)) || root.UserInfo.Length != 0 || root.Fragment.Length != 0)
            throw new UserError("服务地址必须使用 HTTPS。本机服务可使用 HTTP。地址中请勿嵌入密钥。");
        if (path.Length == 0) return root;
        if (root.Query.Length != 0) throw new UserError("API 基础地址不能包含查询参数。");
        string url = root.AbsoluteUri.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) url = url[..^"/chat/completions".Length];
        return new Uri($"{url}/{path}");
    }
}

internal sealed class UserError(string message) : Exception(message);
