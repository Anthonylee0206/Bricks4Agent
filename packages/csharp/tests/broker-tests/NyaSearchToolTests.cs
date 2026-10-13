using Broker.Services;
using Broker.Services.NyaTools;

namespace Broker.Tests;

/// <summary>
/// search 工具契約回歸測試（#2）。
/// 核心修復：mediator 在「零結果」時仍回 Success=true + 罐頭文字（「沒有找到任何可用結果…」），
/// 舊版直接餵回 LLM 當成有效答案。新契約：失敗或零結果一律 Fail + 錯誤碼，讓 LLM 誠實回應。
/// 驗證純函式 <see cref="SearchToolPlugin.Map"/>，與 mediator/DI 解耦。
/// </summary>
public static class NyaSearchToolTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Search Tool Contract Tests (#2) ===");
        Console.WriteLine();

        TestFailurePassesErrorCode();
        TestEmptyResultsBecomeErrorNotCannedText();
        TestCannedNoResultTextRegression();
        TestResultsPersonalized();
        TestResultsDirect();

        Console.WriteLine();
        Console.WriteLine($"=== Search Tool Contract Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestFailurePassesErrorCode()
    {
        Console.WriteLine("--- mediator failure → Fail with error code (#2) ---");
        var result = HighLevelQueryToolResult.Fail("網路搜尋失敗，請稍後再試。", "tool_spec_unavailable");
        var mapped = SearchToolPlugin.Map(result, personalizeOutput: true);
        AssertTrue("not-success", !mapped.Success);
        AssertTrue("carries-error-code", mapped.LlmContent.Contains("tool_spec_unavailable"));
        AssertTrue("is-failure-json", mapped.LlmContent.Contains("\"success\":false"));
    }

    private static void TestEmptyResultsBecomeErrorNotCannedText()
    {
        Console.WriteLine("--- Success=true but zero results → Fail search_no_results (#2) ---");
        var result = new HighLevelQueryToolResult
        {
            Success = true,
            Reply = "搜尋來源：google / 台北即時雨量\n\n沒有找到任何可用結果。",
            Results = Array.Empty<HighLevelQuerySearchResult>()
        };
        var mapped = SearchToolPlugin.Map(result, personalizeOutput: true);
        AssertTrue("empty-not-success", !mapped.Success);
        AssertTrue("empty-error-code", mapped.LlmContent.Contains("search_no_results"));
        // 罐頭文字不得被當成有效 LLM 回覆內容餵回。
        AssertTrue("no-canned-text", !mapped.LlmContent.Contains("沒有找到任何可用結果"));
    }

    private static void TestCannedNoResultTextRegression()
    {
        Console.WriteLine("--- empty Reply also → Fail (#2) ---");
        var result = new HighLevelQueryToolResult
        {
            Success = true,
            Reply = "",
            Results = Array.Empty<HighLevelQuerySearchResult>()
        };
        var mapped = SearchToolPlugin.Map(result, personalizeOutput: false);
        AssertTrue("empty-reply-fail", !mapped.Success);
        AssertTrue("empty-reply-error-code", mapped.LlmContent.Contains("search_no_results"));
    }

    private static void TestResultsPersonalized()
    {
        Console.WriteLine("--- results + personalize → Ok, no DirectReplies (closed loop) ---");
        var result = new HighLevelQueryToolResult
        {
            Success = true,
            Reply = "搜尋來源：google / 台北天氣\n\n1. 台北今天晴",
            Results = new[] { new HighLevelQuerySearchResult { Rank = 1, Title = "台北今天晴" } }
        };
        var mapped = SearchToolPlugin.Map(result, personalizeOutput: true);
        AssertTrue("ok-success", mapped.Success);
        AssertTrue("ok-content", mapped.LlmContent.Contains("台北今天晴"));
        AssertTrue("ok-no-direct", mapped.DirectReplies == null);
    }

    private static void TestResultsDirect()
    {
        Console.WriteLine("--- results + no personalize → Ok with DirectReplies ---");
        var result = new HighLevelQueryToolResult
        {
            Success = true,
            Reply = "搜尋來源：google / x\n\n1. hit",
            Results = new[] { new HighLevelQuerySearchResult { Rank = 1, Title = "hit" } }
        };
        var mapped = SearchToolPlugin.Map(result, personalizeOutput: false);
        AssertTrue("direct-success", mapped.Success);
        AssertTrue("direct-replies-present", mapped.DirectReplies != null && mapped.DirectReplies.Count == 1);
        AssertTrue("direct-replies-content", mapped.DirectReplies != null && mapped.DirectReplies[0].Contains("hit"));
    }

    private static void AssertTrue(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected true"); _failed++; }
    }
}
