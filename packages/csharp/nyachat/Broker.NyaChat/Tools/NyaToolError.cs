namespace Broker.NyaChat;

/// <summary>
/// 工具執行「非預期例外」的回饋淨化（Bug #10 嚴審修復）。
/// </summary>
/// <remarks>
/// 原本 Orchestrator 與 FactToolPlugin 的 catch 把 <c>ex.Message</c> 直接內嵌成 <c>LlmContent</c>
/// 餵回 LLM、再進使用者回覆——DB 錯誤 / 連線字串 / 內部路徑可能外洩,也是 prompt-injection 面。
/// 對「治理平台」不可接受。原始例外只記在 server 端 log / 審計 DB;回饋給 LLM 的一律是泛用訊息。
/// <para>受控的失敗訊息(參數驗證、權限拒絕、空鍵)是作者撰寫的安全字串,不走此淨化。</para>
/// </remarks>
public static class NyaToolError
{
    /// <summary>回饋給 LLM/使用者的泛用內部錯誤訊息(不含任何例外細節)。</summary>
    public const string InternalMessage = "工具執行時發生內部錯誤，請稍後再試。";

    /// <summary>內部錯誤的工具結果 JSON(形狀：<c>{"success":false,"status":"error","error":...}</c>)。</summary>
    public static string InternalJson() => $"{{\"success\":false,\"status\":\"error\",\"error\":\"{InternalMessage}\"}}";
}
