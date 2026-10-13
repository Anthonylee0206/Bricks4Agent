using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LineWorker;

/// <summary>
/// LINE Webhook 接收器
///
/// 獨立的 HTTP listener，接收 LINE Platform 的 webhook 事件。
/// 收到的訊息存入 InboundQueue，供 InboundDispatcher 消費並送入 Broker。
///
/// 訊息類型：
/// - text：文字訊息（審批回覆、指令）
/// - audio：語音訊息（需後續 STT 處理）
/// - image/video/file：媒體訊息（暫記錄 messageId，不自動下載）
/// </summary>
public class WebhookReceiver : IDisposable
{
    private readonly HttpListener _listener;
    private readonly LineApiClient _lineApi;
    private readonly HttpClient _httpClient = new();
    private readonly ILogger _logger;
    private readonly string _audioTempPath;
    private readonly string _brokerApiUrl;

    /// <summary>入站訊息佇列，供外部消費</summary>
    public ConcurrentQueue<InboundMessage> InboundQueue { get; } = new();

    /// <summary>訊息到達時觸發</summary>
    public event Action? OnMessageReceived;

    public WebhookReceiver(int port, string host, LineApiClient lineApi, string audioTempPath, string brokerApiUrl, ILogger logger)
    {
        _lineApi = lineApi;
        _logger = logger;
        _audioTempPath = audioTempPath;
        _brokerApiUrl = brokerApiUrl.TrimEnd('/');

        Directory.CreateDirectory(audioTempPath);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{host}:{port}/webhook/line/");
        _listener.Prefixes.Add($"http://{host}:{port}/api/v1/artifacts/download/");
    }

    /// <summary>啟動 Webhook 監聽</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        _logger.LogInformation("LINE Webhook receiver listening on {Prefixes}", _listener.Prefixes.First());

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(context, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook listener error");
            }
        }

        _listener.Stop();
    }

    private async Task HandleRequest(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            if (request.Url?.AbsolutePath.StartsWith("/api/v1/artifacts/download/", StringComparison.OrdinalIgnoreCase) == true)
            {
                await ProxyArtifactDownloadAsync(request, response, ct);
                return;
            }

            // LINE 的 verify 請求（空 body）直接回 200
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);

            // ── 簽章驗證 ──
            var signature = request.Headers["X-Line-Signature"];
            if (string.IsNullOrEmpty(signature) || !_lineApi.ValidateSignature(body, signature))
            {
                _logger.LogWarning("Invalid webhook signature, rejecting");
                response.StatusCode = 403;
                response.Close();
                return;
            }

            // ── 解析事件 ──
            await ParseAndEnqueue(body, ct);

            response.StatusCode = 200;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook handler error");
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    private async Task ProxyArtifactDownloadAsync(HttpListenerRequest request, HttpListenerResponse response, CancellationToken ct)
    {
        if (request.HttpMethod != "GET")
        {
            response.StatusCode = 405;
            return;
        }

        var pathAndQuery = request.Url?.PathAndQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pathAndQuery))
        {
            response.StatusCode = 404;
            return;
        }

        try
        {
            using var upstream = await _httpClient.GetAsync(_brokerApiUrl + pathAndQuery, HttpCompletionOption.ResponseHeadersRead, ct);
            response.StatusCode = (int)upstream.StatusCode;

            if (upstream.Content.Headers.ContentType != null)
                response.ContentType = upstream.Content.Headers.ContentType.ToString();
            if (upstream.Content.Headers.ContentDisposition != null)
                response.Headers["Content-Disposition"] = upstream.Content.Headers.ContentDisposition.ToString();
            if (upstream.Content.Headers.ContentLength.HasValue)
                response.ContentLength64 = upstream.Content.Headers.ContentLength.Value;

            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(response.OutputStream, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artifact download proxy failed for {PathAndQuery}", pathAndQuery);
            response.StatusCode = 502;
        }
    }

    private async Task ParseAndEnqueue(string body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("events", out var events))
            return;

        foreach (var evt in events.EnumerateArray())
        {
            var type = evt.GetProperty("type").GetString();
            if (type != "message") continue;

            var message = evt.GetProperty("message");
            var messageType = message.GetProperty("type").GetString();
            var messageId = message.GetProperty("id").GetString() ?? "";
            var replyToken = evt.TryGetProperty("replyToken", out var rt) ? rt.GetString() : null;

            var source = evt.GetProperty("source");
            var userId = source.TryGetProperty("userId", out var uid) ? uid.GetString() ?? "" : "";

            var timestamp = evt.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0;

            switch (messageType)
            {
                case "text":
                    var text = message.GetProperty("text").GetString() ?? "";
                    InboundQueue.Enqueue(new InboundMessage
                    {
                        MessageId = messageId,
                        UserId = userId,
                        Type = InboundMessageType.Text,
                        Text = text,
                        ReplyToken = replyToken,
                        Timestamp = timestamp
                    });
                    _logger.LogInformation("Inbound text from {User}: {Text}", userId, Truncate(text, 50));
                    break;

                case "audio":
                    // 下載音訊檔案到暫存
                    var audioPath = await DownloadAudioAsync(messageId, ct);
                    InboundQueue.Enqueue(new InboundMessage
                    {
                        MessageId = messageId,
                        UserId = userId,
                        Type = InboundMessageType.Audio,
                        AudioFilePath = audioPath,
                        ReplyToken = replyToken,
                        Timestamp = timestamp
                    });
                    _logger.LogInformation("Inbound audio from {User}: {Path}", userId, audioPath ?? "download_failed");
                    break;

                default:
                    InboundQueue.Enqueue(new InboundMessage
                    {
                        MessageId = messageId,
                        UserId = userId,
                        Type = InboundMessageType.Other,
                        Text = $"[{messageType} message, id={messageId}]",
                        ReplyToken = replyToken,
                        Timestamp = timestamp
                    });
                    _logger.LogInformation("Inbound {Type} from {User}, messageId={Id}", messageType, userId, messageId);
                    break;
            }

            OnMessageReceived?.Invoke();
        }
    }

    private async Task<string?> DownloadAudioAsync(string messageId, CancellationToken ct)
    {
        try
        {
            var (data, contentType, error) = await _lineApi.GetMessageContentAsync(messageId, ct);
            if (data == null)
            {
                _logger.LogWarning("Failed to download audio {Id}: {Error}", messageId, error);
                return null;
            }

            var ext = contentType switch
            {
                "audio/m4a" => ".m4a",
                "audio/mp4" => ".m4a",
                "audio/mpeg" => ".mp3",
                _ => ".m4a"
            };

            var filePath = Path.Combine(_audioTempPath, $"{messageId}{ext}");
            await File.WriteAllBytesAsync(filePath, data, ct);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio download error for {Id}", messageId);
            return null;
        }
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";

    public void Dispose()
    {
        _listener.Close();
        _httpClient.Dispose();
    }
}

/// <summary>入站訊息</summary>
public class InboundMessage
{
    public string MessageId { get; set; } = "";
    public string UserId { get; set; } = "";
    public InboundMessageType Type { get; set; }
    public string? Text { get; set; }
    public string? AudioFilePath { get; set; }
    public string? ReplyToken { get; set; }
    public long Timestamp { get; set; }
}

public enum InboundMessageType
{
    Text,
    Audio,
    Other
}
