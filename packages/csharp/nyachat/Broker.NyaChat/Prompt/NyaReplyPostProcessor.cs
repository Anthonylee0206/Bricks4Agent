using System.Text.RegularExpressions;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 單一通道的回覆格式化參數。
/// 取代「寫死 LINE」假設：格式化策略仍屬「個性化回覆」職責、留在 Nya，但依通道參數化。
/// </summary>
public sealed class NyaChannelFormatProfile
{
    /// <summary>是否將 Markdown 降級為純文字（LINE 不支援 rich markdown）。</summary>
    public bool StripMarkdown { get; init; }

    /// <summary>單段回覆最大字元數（超過則切割）。</summary>
    public int MaxReplyLength { get; init; }

    /// <summary>是否允許長回覆切割為多段。</summary>
    public bool EnableMultiPartReply { get; init; }
}

/// <summary>
/// 回覆後處理 Pipeline（3 層）：
///   Layer 1 — 清洗：移除 think 標籤、XML 洩漏殘留、多餘空白（所有通道共用）
///   Layer 2 — 格式適配：依通道 profile 決定是否做 Markdown 降級
///   Layer 3 — 長度控制：依通道 profile 切割為多段
/// </summary>
public class NyaReplyPostProcessor
{
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照

    // 預編譯 Regex（效能最佳化）
    private static readonly Regex ThinkTagRegex =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 偵測常見可能洩漏的 XML 控制標籤
    private static readonly string[] LeakedXmlTags =
    {
        "<system_identity>", "</system_identity>",
        "<system_capabilities>", "</system_capabilities>",
        "<user_profile>", "</user_profile>",
        "<long_term_memory>", "</long_term_memory>",
        "<rag_context>", "</rag_context>",
        "<fact_extraction_instruction>", "</fact_extraction_instruction>",
        "<summarization_instruction>", "</summarization_instruction>",
        "<output_format>", "</output_format>"
    };

    private static readonly Regex HtmlTagRegex =
        new(@"<[a-zA-Z][^>]*>|</[a-zA-Z]+>", RegexOptions.Compiled);

    private static readonly Regex MarkdownLinkRegex =
        new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    private static readonly Regex CodeBlockRegex =
        new(@"```[\w]*\n([\s\S]*?)```", RegexOptions.Compiled);

    private static readonly Regex H1Regex = new(@"^# (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex H2Regex = new(@"^## (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex H3Regex = new(@"^### (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex MultiNewlineRegex =
        new(@"\n{3,}", RegexOptions.Compiled);

    public NyaReplyPostProcessor(INyaConfigStore<NyaChatConfig> configStore)
    {
        _configStore = configStore;
    }

    /// <summary>
    /// 對 LLM 原始回覆進行全 pipeline 處理（預設以 line 通道 profile），回傳 1 或多段回覆。
    /// </summary>
    public List<string> Process(string? rawReply) => Process(rawReply, "line");

    /// <summary>
    /// 依指定通道的格式化設定檔對 LLM 原始回覆進行全 pipeline 處理。
    /// </summary>
    public List<string> Process(string? rawReply, string channelType)
    {
        if (string.IsNullOrWhiteSpace(rawReply))
            return new List<string> { "" };

        var profile = ResolveProfile(channelType);
        var text = rawReply;

        // Layer 1: 清洗（所有通道共用）
        text = Sanitize(text);

        // Layer 2: 格式適配（依 profile）
        if (profile.StripMarkdown)
            text = FormatForLine(text);

        // 最終 trim
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return new List<string> { "" };

        // Layer 3: 長度控制與切割（依 profile）
        return SplitIfNeeded(text, profile);
    }

    /// <summary>
    /// 由通道類型解析格式化 profile。
    /// line = 沿用現行設定（零回歸）；其餘通道採預設（不降 markdown、不多段）。
    /// 更細致的 per-channel 配置結構留待後續擴充（避免與 config 重構衝突）。
    /// </summary>
    private NyaChannelFormatProfile ResolveProfile(string channelType)
    {
        var cfg = _config;
        if (string.Equals(channelType, "line", StringComparison.OrdinalIgnoreCase))
            return new NyaChannelFormatProfile
            {
                StripMarkdown        = cfg.StripMarkdown,
                MaxReplyLength       = cfg.MaxReplyLength,
                EnableMultiPartReply = cfg.EnableMultiPartReply
            };

        return new NyaChannelFormatProfile
        {
            StripMarkdown        = false,
            MaxReplyLength       = cfg.MaxReplyLength,
            EnableMultiPartReply = false
        };
    }

    // ── Layer 1: 清洗 ────────────────────────────────────────────────────────

    private string Sanitize(string text)
    {
        // 移除 <think>...</think> 標籤
        text = ThinkTagRegex.Replace(text, "");

        // 移除洩漏的系統 XML 標籤（只移除標籤本身，保留內容）
        foreach (var tag in LeakedXmlTags)
            text = text.Replace(tag, "", StringComparison.OrdinalIgnoreCase);

        // 移除多餘空白行（3+ 換行 → 2 換行）
        text = MultiNewlineRegex.Replace(text, "\n\n");

        return text;
    }

    // ── Layer 2: LINE 格式適配 ───────────────────────────────────────────────

    private string FormatForLine(string text)
    {
        // 程式碼區塊：轉為縮排格式（LINE 不支援 syntax highlighting）
        text = CodeBlockRegex.Replace(text, m =>
        {
            var code = m.Groups[1].Value.TrimEnd();
            var indented = string.Join("\n",
                code.Split('\n').Select(l => "  " + l));
            return $"────────────\n{indented}\n────────────";
        });

        // 標題轉換
        text = H3Regex.Replace(text, "【$1】");
        text = H2Regex.Replace(text, "【$1】");
        text = H1Regex.Replace(text, "【$1】");

        // Markdown 連結：轉為純文字 + URL
        text = MarkdownLinkRegex.Replace(text, m =>
        {
            var linkText = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            if (string.Equals(linkText, url, StringComparison.OrdinalIgnoreCase))
                return url; // 純 URL，不重複
            return $"{linkText}\n{url}";
        });

        // 移除殘留 HTML 標籤（保留內容）
        text = HtmlTagRegex.Replace(text, "");

        return text;
    }

    // ── Layer 3: 長度控制 ────────────────────────────────────────────────────

    private static List<string> SplitIfNeeded(string text, NyaChannelFormatProfile profile)
    {
        if (!profile.EnableMultiPartReply || text.Length <= profile.MaxReplyLength)
            return new List<string> { text };

        var parts = new List<string>();
        var remaining = text;

        while (remaining.Length > profile.MaxReplyLength)
        {
            var splitAt = FindSplitPoint(remaining, profile.MaxReplyLength);
            parts.Add(remaining[..splitAt].TrimEnd());
            remaining = remaining[splitAt..].TrimStart();
        }

        if (!string.IsNullOrWhiteSpace(remaining))
            parts.Add(remaining);

        return parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static int FindSplitPoint(string text, int maxLength)
    {
        var candidate = maxLength;

        // 優先：段落邊界（\n\n）
        var paraBreak = text.LastIndexOf("\n\n", candidate, StringComparison.Ordinal);
        if (paraBreak > maxLength / 2)
            return paraBreak + 2;

        // 次選：單換行
        var lineBreak = text.LastIndexOf('\n', candidate);
        if (lineBreak > maxLength / 2)
            return lineBreak + 1;

        // 末選：句號、問號、驚嘆號
        for (var i = candidate; i > maxLength / 2; i--)
        {
            if (text[i] is '。' or '！' or '？' or '.' or '!' or '?')
                return i + 1;
        }

        // 最後手段：硬切
        return candidate;
    }
}
