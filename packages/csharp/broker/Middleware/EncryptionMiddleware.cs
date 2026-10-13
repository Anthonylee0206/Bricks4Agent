using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrokerCore.Crypto;
using Broker.Helpers;

namespace Broker.Middleware;

/// <summary>
/// 加密中介軟體 —— 管線第一層（在 Auth / Audit 之前）
///
/// 職責：
/// 1. 讀取 POST body（EncryptedRequest JSON）
/// 2. 依 session_id 查找 session_key（或對初始交握做 ECDH）
/// 3. 驗證 seq &gt; last_seen_seq（防 replay）
/// 4. AES-256-GCM 解密 → 明文注入 HttpContext.Items
/// 5. endpoint 回傳後，攔截 response body → AES-256-GCM 加密
/// 6. 寫出加密信封
///
/// 排除路徑：
/// - /api/v1/health（load balancer 探測；端點政策為 Public）
/// - 非 POST 請求（HttpMethods.IsPost，不分大小寫）
/// </summary>
public class EncryptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IEnvelopeCrypto _crypto;
    private readonly ISessionKeyStore _keyStore;
    private readonly ILogger<EncryptionMiddleware> _logger;

    // HttpContext.Items 鍵名
    public const string DecryptedBodyKey = "decrypted_body";
    public const string SessionIdKey = "encryption_session_id";
    public const string RequestSeqKey = "encryption_request_seq";
    public const string SessionKeyKey = "encryption_session_key";
    public const string IsHandshakeKey = "encryption_is_handshake";
    public const string ClientEphemeralPubKey = "encryption_client_pub";

    // 排除加密的路徑
    private static readonly HashSet<string> ExcludedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/health"
    };

    private static bool IsPlainJsonTrustedPath(string path)
    {
        return path.StartsWith("/api/v1/high-level/line/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/tool-specs/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/portal/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/local-admin/", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public EncryptionMiddleware(
        RequestDelegate next,
        IEnvelopeCrypto crypto,
        ISessionKeyStore keyStore,
        ILogger<EncryptionMiddleware> logger)
    {
        _next = next;
        _crypto = crypto;
        _keyStore = keyStore;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // 排除不需加密的端點
        if (ExcludedPaths.Contains(path)
            || BrokerAuthPolicyResolver.Resolve(context) == BrokerAuthPolicy.Public
            || path.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 信任路徑（local-admin / tool-specs / line）：POST / PUT / PATCH 都讀明文 body
        if (IsPlainJsonTrustedPath(path))
        {
            if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method))
            {
                context.Request.EnableBuffering();
                using (var plainReader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
                {
                    var plainBody = await plainReader.ReadToEndAsync();
                    context.Items[DecryptedBodyKey] = string.IsNullOrWhiteSpace(plainBody) ? "{}" : plainBody;
                }
                context.Request.Body.Position = 0;
            }
            await _next(context);
            return;
        }

        // 非信任路徑只對 POST 做加解密
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // ── 1. 讀取並解析 EncryptedRequest ──
        // L-5 修復：縱深防禦 — 即使上游有 BodySizeLimitMiddleware，此處也檢查 Content-Length
        const long EncryptionMaxBodyBytes = 2_097_152; // 2MB（加密 envelope 比明文大）
        if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > EncryptionMaxBodyBytes)
        {
            await WriteEncryptionError(context, 413, $"Encrypted body too large (max {EncryptionMaxBodyBytes} bytes).");
            return;
        }

        string requestBody;
        context.Request.EnableBuffering();
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(requestBody))
        {
            await WriteEncryptionError(context, 400, "Empty request body.");
            return;
        }

        EncryptedRequest? encryptedReq;
        try
        {
            encryptedReq = JsonSerializer.Deserialize<EncryptedRequest>(requestBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse encrypted request envelope");
            await WriteEncryptionError(context, 400, "Invalid envelope format.");
            return;
        }

        if (encryptedReq?.Envelope == null)
        {
            await WriteEncryptionError(context, 400, "Missing envelope.");
            return;
        }

        // ── 2. 判斷交握 vs 已建立 session ──
        string decryptedBody;

        var isHandshake = !string.IsNullOrEmpty(encryptedReq.ClientEphemeralPub)
                          && string.IsNullOrEmpty(encryptedReq.SessionId);

        if (isHandshake)
        {
            // ── 初始交握（session 註冊） ──
            // 交握信封沒有 session 通道，只能用來建立 session；送往其他端點一律拒絕，
            // 避免 token 經由交握信封送出而繞過通道與 session 的綁定。
            if (BrokerAuthPolicyResolver.Resolve(context) != BrokerAuthPolicy.SessionBootstrap)
            {
                _logger.LogWarning("Handshake envelope rejected for {Path}", path);
                await WriteEncryptionError(context, 400, "Handshake envelopes are only accepted for session registration.");
                return;
            }

            try
            {
                decryptedBody = _crypto.DecryptHandshake(
                    encryptedReq.ClientEphemeralPub!,
                    encryptedReq.Envelope);

                context.Items[IsHandshakeKey] = true;
                context.Items[ClientEphemeralPubKey] = encryptedReq.ClientEphemeralPub;
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex, "Handshake decryption failed");
                await WriteEncryptionError(context, 400, "Handshake decryption failed.");
                return;
            }
        }
        else if (!string.IsNullOrEmpty(encryptedReq.SessionId))
        {
            // ── 已建立 session 的加密請求 ──
            var sessionId = encryptedReq.SessionId;
            var seq = encryptedReq.Envelope.Seq;

            // 2a. 查找 session_key
            var sessionKey = _keyStore.Retrieve(sessionId);
            if (sessionKey == null)
            {
                _logger.LogWarning("Session key not found: {SessionId}", sessionId);
                await WriteEncryptionError(context, 401, "Invalid or expired session.");
                return;
            }

            // 2b. Replay 防護：seq > last_seen_seq
            if (!_keyStore.TryAdvanceSeq(sessionId, seq))
            {
                _logger.LogWarning("Replay detected: session={SessionId}, seq={Seq}", sessionId, seq);
                await WriteEncryptionError(context, 400, "Replay detected: invalid sequence number.");
                return;
            }

            // 2c. 解密
            try
            {
                // AAD = direction + session_id + seq + endpoint_path（H-1 修復：加方向標記）
                var aad = $"req:{sessionId}{seq}{path}";
                decryptedBody = _crypto.Decrypt(encryptedReq.Envelope, sessionKey, aad);
            }
            catch (CryptographicException ex)
            {
                _logger.LogWarning(ex, "Decryption failed: session={SessionId}", sessionId);
                await WriteEncryptionError(context, 400, "Decryption failed: tampered or invalid envelope.");
                return;
            }

            context.Items[SessionIdKey] = sessionId;
            context.Items[RequestSeqKey] = seq;
            context.Items[SessionKeyKey] = sessionKey;
        }
        else
        {
            await WriteEncryptionError(context, 400, "Missing session_id or client_ephemeral_pub.");
            return;
        }

        // ── 3. 注入明文 body 到 HttpContext ──
        context.Items[DecryptedBodyKey] = decryptedBody;

        // 替換 request body 為明文（後續 middleware/endpoint 可直接讀取）
        var plaintextBytes = Encoding.UTF8.GetBytes(decryptedBody);
        context.Request.Body = new MemoryStream(plaintextBytes);
        context.Request.ContentLength = plaintextBytes.Length;
        context.Request.ContentType = "application/json";

        // ── 4. 攔截 response body ──
        var originalBodyStream = context.Response.Body;
        using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        // ── 5. 執行後續管線 ──
        await _next(context);

        // ── 6. 加密 response body ──（M-5 修復：StreamReader 使用 using 確保釋放）
        responseBuffer.Seek(0, SeekOrigin.Begin);
        string responseBody;
        using (var responseReader = new StreamReader(responseBuffer, leaveOpen: true))
        {
            responseBody = await responseReader.ReadToEndAsync();
        }

        if (!string.IsNullOrEmpty(responseBody))
        {
            byte[]? sessionKey2 = null;
            int responseSeq = 0;

            if (context.Items.TryGetValue(SessionKeyKey, out var skObj) && skObj is byte[] sk)
            {
                sessionKey2 = sk;
                responseSeq = context.Items.TryGetValue(RequestSeqKey, out var seqObj) && seqObj is int s ? s : 0;
            }

            if (sessionKey2 != null)
            {
                // 用 session_key 加密回應
                var sessionIdStr = context.Items[SessionIdKey] as string ?? "";
                var responseAad = $"resp:{sessionIdStr}{responseSeq}{path}";
                var encryptedEnvelope = _crypto.Encrypt(responseBody, sessionKey2, responseSeq, responseAad);

                var encryptedResponse = new EncryptedResponse { V = 1, Envelope = encryptedEnvelope };

                // 初始交握回應：額外包含 session_id（明文），
                // 使 Client 可先讀取 session_id → derive session_key → 解密 envelope
                var isHandshakeResp = context.Items.TryGetValue(IsHandshakeKey, out var hsObj) && hsObj is true;
                if (isHandshakeResp && !string.IsNullOrEmpty(sessionIdStr))
                {
                    encryptedResponse.SessionId = sessionIdStr;
                }

                var encryptedJson = JsonSerializer.Serialize(encryptedResponse, JsonOptions);

                context.Response.ContentType = "application/json";
                context.Response.Body = originalBodyStream;
                await context.Response.WriteAsync(encryptedJson);
            }
            else
            {
                // 無 session_key 的回應（不應發生在正常流程中）
                context.Response.Body = originalBodyStream;
                responseBuffer.Seek(0, SeekOrigin.Begin);
                await responseBuffer.CopyToAsync(originalBodyStream);
            }
        }
        else
        {
            context.Response.Body = originalBodyStream;
        }
    }

    /// <summary>
    /// 寫出未加密的錯誤回應（加密層本身的錯誤無法加密）
    /// M-10 修復：統一使用 ApiResponseHelper 格式
    /// </summary>
    private static async Task WriteEncryptionError(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(ApiResponseHelper.Error(message, statusCode));
    }
}

/// <summary>
/// EncryptionMiddleware 的擴展方法
/// </summary>
public static class EncryptionMiddlewareExtensions
{
    public static IApplicationBuilder UseEnvelopeEncryption(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<EncryptionMiddleware>();
    }
}
