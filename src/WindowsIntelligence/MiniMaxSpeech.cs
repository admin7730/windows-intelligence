using System.Net.Http.Json;
using System.Text.Json;
using NAudio.Wave;

namespace WindowsIntelligence;

internal static class MiniMaxSpeech
{
    internal static readonly Guid CredentialId = new("87c24ac3-d04a-4743-88d1-1aca0089585d");
    internal const string HighDefinition = "speech-2.8-hd", Turbo = "speech-2.8-turbo";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(28), MaxResponseContentBufferSize = 24 * 1024 * 1024 };

    internal static async Task<WaveStream> CreateAsync(string text, Settings settings, CancellationToken ct, HttpClient? transport = null, string? key = null)
    {
        key ??= SecretStore.Get(CredentialId);
        if (string.IsNullOrWhiteSpace(key)) throw new UserError("MiniMax 凭据尚未配置。");
        string? primaryFailure = null;
        foreach (var model in new[] { HighDefinition, Turbo })
        {
            ct.ThrowIfCancellationRequested();
            try { return await RequestAsync(text, settings, model, key, transport ?? Http, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is (UserError or HttpRequestException or OperationCanceledException or JsonException or FormatException or InvalidDataException or IOException or System.Runtime.InteropServices.COMException))
            {
                var message = ex switch { UserError => ex.Message, OperationCanceledException => "请求超时", HttpRequestException => "无法连接 MiniMax", _ => "语音响应格式异常" };
                if (primaryFailure is not null) throw new UserError($"MiniMax HD：{primaryFailure}；Turbo：{message}。");
                primaryFailure = message;
            }
        }
        throw new InvalidOperationException();
    }

    private static async Task<WaveStream> RequestAsync(string text, Settings settings, string model, string key, HttpClient http, CancellationToken ct)
    {
        using var request = AiClient.Create(HttpMethod.Post, new Uri("https://api.minimaxi.com/v1/t2a_v2"), key);
        request.Content = JsonContent.Create(new
        {
            model, text, stream = false, output_format = "hex", language_boost = "auto",
            voice_setting = new { voice_id = string.IsNullOrWhiteSpace(settings.MiniMaxVoice) ? "male-qn-qingse" : settings.MiniMaxVoice.Trim(), speed = Math.Clamp(settings.TtsSpeed,.5,2), vol = 1, pitch = 0 },
            audio_setting = new { sample_rate = 32000, bitrate = 128000, format = "mp3", channel = 1 }
        });
        using var response = await http.SendAsync(request,ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UserError($"HTTP {(int)response.StatusCode}");
        await using var raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(raw, cancellationToken: ct).ConfigureAwait(false);
        var result = json.RootElement;
        if (result.TryGetProperty("base_resp",out var status) && status.ValueKind == JsonValueKind.Object && status.TryGetProperty("status_code",out var code) && code.TryGetInt32(out var value) && value != 0)
            throw new UserError(value switch { 1004 or 2049 => $"凭据或账户无权合成（{value}）", 1008 => "账户额度不足（1008）", _ => $"服务状态 {value}" });
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("audio",out var audio) || audio.ValueKind != JsonValueKind.String || audio.GetString() is not { Length: > 0 } hex)
            throw new UserError("服务未返回音频");
        var stream = new MemoryStream(Convert.FromHexString(hex));
        try { return new Mp3FileReader(stream); }
        catch { stream.Dispose(); throw; }
    }
}
