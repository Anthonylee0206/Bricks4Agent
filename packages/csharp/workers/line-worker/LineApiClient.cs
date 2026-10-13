using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LineWorker;

/// <summary>
/// LINE Messaging API 客戶端
///
/// 封裝 Push Message、Reply Message、取得內容等 API 呼叫。
/// 不依賴第三方 LINE SDK，直接使用 HTTP REST API。
/// </summary>
public class LineApiClient : ILineApiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _channelSecret;
    private readonly string _channelAccessToken;

    private const string ApiBase = "https://api.line.me/v2/bot";
    private const string ApiDataBase = "https://api-data.line.me/v2/bot";

    public LineApiClient(string channelAccessToken, string channelSecret)
    {
        _channelAccessToken = channelAccessToken;
        _channelSecret = channelSecret;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", channelAccessToken);
    }

    // ─── 發送訊息 ───

    /// <summary>Push 文字訊息給指定使用者</summary>
    public async Task<(bool Success, string? Error)> PushTextMessageAsync(
        string recipientId, string text, CancellationToken ct = default)
    {
        var body = new
        {
            to = recipientId,
            messages = new[]
            {
                new { type = "text", text }
            }
        };

        return await PostAsync($"{ApiBase}/message/push", body, ct);
    }

    /// <summary>Push 多則訊息（最多 5 則）</summary>
    public async Task<(bool Success, string? Error)> PushMessagesAsync(
        string recipientId, object[] messages, CancellationToken ct = default)
    {
        var body = new
        {
            to = recipientId,
            messages
        };

        return await PostAsync($"{ApiBase}/message/push", body, ct);
    }

    /// <summary>Push 音訊訊息</summary>
    public async Task<(bool Success, string? Error)> PushAudioMessageAsync(
        string recipientId, string audioUrl, int durationMs, CancellationToken ct = default)
    {
        var body = new
        {
            to = recipientId,
            messages = new[]
            {
                new
                {
                    type = "audio",
                    originalContentUrl = audioUrl,
                    duration = durationMs
                }
            }
        };

        return await PostAsync($"{ApiBase}/message/push", body, ct);
    }

    /// <summary>Reply 訊息（回覆 webhook 事件）</summary>
    public async Task<(bool Success, string? Error)> ReplyTextMessageAsync(
        string replyToken, string text, CancellationToken ct = default)
    {
        var body = new
        {
            replyToken,
            messages = new[]
            {
                new { type = "text", text }
            }
        };

        return await PostAsync($"{ApiBase}/message/reply", body, ct);
    }

    // ─── 取得內容 ───

    /// <summary>下載使用者傳送的媒體內容（圖片、音訊、影片）</summary>
    public async Task<(byte[]? Data, string? ContentType, string? Error)> GetMessageContentAsync(
        string messageId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{ApiDataBase}/message/{messageId}/content", ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                return (null, null, $"LINE API error {response.StatusCode}: {errorBody}");
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            return (data, contentType, null);
        }
        catch (Exception ex)
        {
            return (null, null, $"GetContent error: {ex.Message}");
        }
    }

    // ─── Webhook 簽章驗證 ───

    /// <summary>驗證 LINE Webhook 簽章（HMAC-SHA256）</summary>
    public bool ValidateSignature(string body, string signature)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_channelSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        var expected = Convert.ToBase64String(hash);
        return expected == signature;
    }

    // ─── 內部 ───

    private async Task<(bool Success, string? Error)> PostAsync(
        string url, object body, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content, ct);

            if (response.IsSuccessStatusCode)
                return (true, null);

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            return (false, $"LINE API error {response.StatusCode}: {errorBody}");
        }
        catch (Exception ex)
        {
            return (false, $"LINE API call failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
