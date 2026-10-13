namespace Broker.NyaChat;

/// <summary>
/// 輕量 token 估算器。
/// </summary>
/// <remarks>
/// <para>
/// 以字元類別近似 token 數：CJK 字元 ≈ <see cref="_cjkPerChar"/>（預設 1.5）token/char、
/// 其餘（拉丁/數字/標點/空白）≈ <see cref="_latinPerChar"/>（預設 0.25）token/char。
/// </para>
/// <para>
/// <b>用途限定</b>（審查2）：僅供<b>預算分配與截斷觸發</b>，<b>不</b>用於計費、<b>不</b>用於硬切單則訊息中段。
/// 不追求 tokenizer 級精確；以 <c>ContextSafetyMargin</c> 吸收估算誤差。純函式、無相依、可單測。
/// </para>
/// <para>
/// 比率為啟動期固定的啟發式旋鈕（由 <c>AddNyaChat</c> 以 seed 配置建構）；預算數值本身
/// （MaxContextTokens / 各 ratio / 上限）則經 6D 配置熱重載，在 Orchestrator 讀取當前快照。
/// </para>
/// </remarks>
public sealed class NyaTokenEstimator
{
    private readonly double _cjkPerChar;
    private readonly double _latinPerChar;

    /// <summary>預設比率的共用實例（供無預算的相容路徑使用）。</summary>
    public static readonly NyaTokenEstimator Default = new();

    public NyaTokenEstimator(double cjkPerChar = 1.5, double latinPerChar = 0.25)
    {
        _cjkPerChar   = cjkPerChar   > 0 ? cjkPerChar   : 1.5;
        _latinPerChar = latinPerChar > 0 ? latinPerChar : 0.25;
    }

    /// <summary>估算單段文字的 token 數（向上取整）。</summary>
    public int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        double total = 0;
        foreach (var ch in text)
            total += IsCjk(ch) ? _cjkPerChar : _latinPerChar;

        return (int)Math.Ceiling(total);
    }

    /// <summary>
    /// 估算一組 LLM 訊息的 token 數，含每則訊息的結構固定開銷（role / 分隔符 ≈ 4 token/則）。
    /// </summary>
    public int Estimate(IEnumerable<NyaLlmMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            total += 4; // 每則訊息的角色/包裝固定開銷
            total += Estimate(m.Content);
            if (m.ToolCalls != null)
                foreach (var tc in m.ToolCalls)
                    total += Estimate(tc.FunctionName) + Estimate(tc.FunctionArguments) + 4;
            if (!string.IsNullOrEmpty(m.Name)) total += Estimate(m.Name);
        }
        return total;
    }

    /// <summary>
    /// 將文字截斷至約 <paramref name="maxTokens"/> token（保留頭部，超出時尾加標記）。
    /// 供工具結果預算（3D）使用——只截「整段尾部」，不破壞語意中段。
    /// </summary>
    public string TruncateToTokens(string? text, int maxTokens, string marker = "…（結果過長，已截斷）")
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0) return text ?? "";
        if (Estimate(text) <= maxTokens) return text;

        // 以最壞情況（全 CJK）反推可保留字元數，確保截斷後一定不超標
        var keepChars = Math.Max(1, (int)(maxTokens / _cjkPerChar));
        if (keepChars >= text.Length) return text;
        return text[..keepChars].TrimEnd() + marker;
    }

    /// <summary>是否為 CJK 表意/假名/韓文字元（粗略範圍，估算用足夠）。</summary>
    private static bool IsCjk(char ch)
    {
        int c = ch;
        return c is >= 0x3000 and <= 0x9FFF      // CJK 標點 + 假名 + 統一表意
                 or >= 0xAC00 and <= 0xD7AF      // 韓文音節
                 or >= 0xF900 and <= 0xFAFF      // CJK 相容表意
                 or >= 0xFF00 and <= 0xFFEF;     // 全形字元
    }
}
