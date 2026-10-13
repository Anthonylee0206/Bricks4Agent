namespace Broker.NyaChat;

/// <summary>
/// 通道無關的使用者識別正規化。
/// 入口以 <c>(channelType, channelUserId)</c> 進入，由此產生 Nya 內部統一 userId，
/// 避免不同通道的同名 id 共用記憶。
/// </summary>
public static class NyaUserIdentity
{
    /// <summary>
    /// 產生內部 userId。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>遷移安全（硬約束）</b>：<c>line</c> 通道必須回傳原 id。既有
    /// <c>nya_messages</c> / <c>nya_facts</c> / <c>nya_soul_bindings</c> 全部以 LINE 原始 userId 為 key，
    /// 任何「一律加前綴」的寫法都會讓所有現存 LINE 使用者的記憶 / 事實 / Soul 綁定瞬間孤兒化。
    /// 其餘通道加 <c>channel:</c> 前綴做命名空間隔離。
    /// </remarks>
    public static string Normalize(string channelType, string channelUserId)
    {
        if (string.IsNullOrWhiteSpace(channelUserId))
            return channelUserId ?? "";

        return string.Equals(channelType, "line", StringComparison.OrdinalIgnoreCase)
            ? channelUserId
            : $"{channelType.ToLowerInvariant()}:{channelUserId}";
    }
}
