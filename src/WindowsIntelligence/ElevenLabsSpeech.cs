using System.Net.Http.Json;
using NAudio.Wave;

namespace WindowsIntelligence;

internal static class ElevenLabsSpeech
{
    internal static readonly Guid CredentialId = new("a80d2f6e-7f31-409b-9cb2-7f74265a1f8c");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(40) };

    public static async Task<WaveStream> CreateAsync(string text, Settings settings, CancellationToken ct, HttpClient? transport = null, string? key = null)
    {
        key ??= SecretStore.Get(CredentialId);
        if (string.IsNullOrWhiteSpace(key)) throw new UserError("请先在语音朗读设置中保存 ElevenLabs 凭据。");
        var voice = settings.ElevenLabsVoice.Trim();
        var model = settings.ElevenLabsModel.Trim();
        if (model.Length == 0 || voice.Length == 0 || !voice.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new UserError("请填写有效的 ElevenLabs Voice ID 和模型。");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{voice}?output_format=mp3_44100_128");
        request.Headers.Add("xi-api-key", key.Trim());
        var body = new Dictionary<string,object> { ["text"] = text, ["model_id"] = model };
        if (!model.StartsWith("eleven_v4", StringComparison.OrdinalIgnoreCase) && Math.Abs(settings.TtsSpeed - 1) > .01)
            body["voice_settings"] = new { speed = Math.Clamp(settings.TtsSpeed,.5,2) };
        request.Content = JsonContent.Create(body);
        using var response = await (transport ?? Http).SendAsync(request,ct).ConfigureAwait(false);
        switch ((int)response.StatusCode)
        {
            case 401: case 403: throw new UserError("ElevenLabs 凭据无效或没有语音合成权限。");
            case 402: throw new UserError("当前 ElevenLabs 套餐或额度不支持该声音。");
            case 422: throw new UserError("ElevenLabs 无法使用当前模型、声音或语音参数。");
            case 429: throw new UserError("ElevenLabs 请求过多，请稍后再试。");
        }
        if (!response.IsSuccessStatusCode) throw new UserError($"ElevenLabs 返回 HTTP {(int)response.StatusCode}。");
        if (response.Content.Headers.ContentType?.MediaType is not ("audio/mpeg" or "audio/mp3" or "application/octet-stream"))
            throw new UserError("ElevenLabs 没有返回 MP3 音频。");
        var buffer = new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        try { return new Mp3FileReader(buffer); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Runtime.InteropServices.COMException)
        { buffer.Dispose(); throw new UserError("无法解码 ElevenLabs 音频。"); }
    }
}
