namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 工具權限判定契約。由 <b>broker 治理層</b>提供實作（Nya 不自建一套權限系統，保持與權限解耦）。
/// </summary>
/// <remarks>
/// 同時用於兩個時點：
/// (1) 建構 function-calling 工具清單時，決定是否把工具暴露給 LLM；
/// (2) 工具執行前再次檢查（防止 LLM 繞過）。
/// 「只有管理者能用量化交易工具」即由 broker 的實作（如 <c>BrokerToolAuthorizer</c>）決定。
/// </remarks>
public interface INyaToolAuthorizer
{
    /// <summary>判定指定使用者 / 通道是否可使用某工具。</summary>
    bool CanUse(string userId, string channelType, NyaToolSchema tool);
}
