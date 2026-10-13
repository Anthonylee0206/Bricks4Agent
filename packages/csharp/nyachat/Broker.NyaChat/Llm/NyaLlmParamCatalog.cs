namespace Broker.NyaChat;

/// <summary>單一參數的目錄條目（Doc 3 Plan C §5）。doc 只說上下限與高低後果，不舉例。</summary>
public sealed record NyaLlmParamSpec(
    string Key, string Type, double? Min, double? Max, string Doc, bool Common, string[] AppliesTo);

/// <summary>
/// 靜態參數目錄：同時驅動 UI 表單常見欄位、JSON 預覽、每參數說明、min/max 驗證（Doc 3 Plan C §5）。
/// </summary>
public static class NyaLlmParamCatalog
{
    public static readonly IReadOnlyList<NyaLlmParamSpec> All = new[]
    {
        new NyaLlmParamSpec("temperature", "number", 0, 2,
            "取樣溫度。越高越發散有創意、越低越確定保守。", true, new[] { "*" }),
        new NyaLlmParamSpec("top_p", "number", 0, 1,
            "核取樣機率質量。越低越集中於高機率詞、越高越多樣。", true, new[] { "*" }),
        new NyaLlmParamSpec("max_context_tokens", "number", 0, null,
            "context 視窗上限。越大可塞越多脈絡、越小越省成本且越易截斷。", true, new[] { "*" }),
        new NyaLlmParamSpec("timeout_seconds", "number", 0, null,
            "請求逾時秒數。越大越能容忍慢模型、越小越快放棄。", true, new[] { "*" }),
        new NyaLlmParamSpec("enable_thinking", "bool", null, null,
            "是否開啟思考。開啟推理更深但更慢更貴、關閉更快。", false, new[] { "ollama", "openai_chat" }),
        new NyaLlmParamSpec("thinking_budget", "number", 0, null,
            "思考 token 預算。越大推理越深越慢、越小越快。", false, new[] { "ollama", "openai_chat" }),
        new NyaLlmParamSpec("frequency_penalty", "number", -2, 2,
            "頻率懲罰。越高越壓抑重複用詞、越低越放任；本地模型常需 >0 才不鬼打牆。", false, new[] { "openai_chat", "openai_responses" }),
        new NyaLlmParamSpec("presence_penalty", "number", -2, 2,
            "存在懲罰。越高越鼓勵換新主題/詞、越低越黏著既有內容。", false, new[] { "openai_chat", "openai_responses" }),
        new NyaLlmParamSpec("repeat_penalty", "number", 0, 2,
            "重複懲罰(Ollama/llama.cpp 風格)。>1 抑制重複、=1 不懲罰；思考型本地模型建議 1.1~1.3。", false, new[] { "ollama" }),
    };
}
