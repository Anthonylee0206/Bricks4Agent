using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 工具實際執行狀態（三態）：供閉環二次回覆前，於每筆 <c>role=tool</c> 結果前明確標註，
/// 讓 LLM 看得到「這個工具到底有沒有跑、成不成功」，而非把狀態埋在內容/JSON 裡。
/// </summary>
public enum NyaToolExecStatus
{
    /// <summary>工具成功執行並回傳結果。</summary>
    Success,

    /// <summary>工具有嘗試執行但未成功（業務失敗、參數錯誤、執行期例外）。</summary>
    Failed,

    /// <summary>工具根本沒有執行：功能關閉、無權限或不存在（執行門擋下）。</summary>
    NotEnabled
}

/// <summary>
/// 把工具執行狀態 + 結果內容組成「標註狀態」的 <c>role=tool</c> 文字，餵回給第二次 LLM 呼叫。
/// 純函式（無狀態），與 <see cref="NyaToolReplyPlanner"/> 同層，方便單元測試。
/// </summary>
/// <remarks>
/// 新增功能：主對話閉環要把工具執行結果狀態（成功 / 失敗 / 未啟動）明確告知 LLM，
/// 搭配 <c>tool_result_personalize</c> 提示詞——若上文沒有對應的工具執行結果，LLM 卻回報工具做了什麼，即為捏造。
/// </remarks>
public static class NyaToolStatusFormatter
{
    /// <summary>由工具回傳結果推導三態狀態。NotEnabled 由執行門另行判定，不在此推導。</summary>
    public static NyaToolExecStatus FromResult(NyaToolResult result)
        => result.Success ? NyaToolExecStatus.Success : NyaToolExecStatus.Failed;

    /// <summary>狀態的中文標籤（含括號說明，讓模型清楚每一態的語意）。</summary>
    public static string Label(NyaToolExecStatus status) => status switch
    {
        NyaToolExecStatus.Success    => "成功",
        NyaToolExecStatus.Failed     => "失敗（工具已執行但未成功）",
        NyaToolExecStatus.NotEnabled => "未啟動（功能關閉／無權限／不存在，並未實際執行）",
        _                            => "未知"
    };

    /// <summary>
    /// 組出標註狀態的工具結果文字：首行為 <c>[工具執行狀態：…]</c>，其後為原始結果內容。
    /// 狀態標籤永遠在最前、不被內容截斷影響（呼叫端應先截斷內容、再呼叫本方法）。
    /// </summary>
    public static string FormatForLlm(NyaToolExecStatus status, string content)
    {
        var body = string.IsNullOrEmpty(content) ? "(no content)" : content;
        return $"[工具執行狀態：{Label(status)}]\n{body}";
    }
}
