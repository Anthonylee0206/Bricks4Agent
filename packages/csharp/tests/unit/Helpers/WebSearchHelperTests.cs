using System.Reflection;
using Broker.Helpers;

namespace Unit.Tests.Helpers;

/// <summary>
/// 鎖住 WebSearchHelper 的確定性純邏輯(不碰 HttpClient / IO):
///   - ResolveWikipediaLanguages：locale 前綴決定 zh/en 偏好順序
///   - NormalizeWikipediaExtract：空白壓縮 + trim
///   - ShortenText：超長截斷 + 省略號(…)、未超長原樣透傳
///   - HtmlToText：script/style 移除、區塊標籤→換行、去標籤、實體解碼、空白壓縮
///   - ExtractTimeCandidates：HH:MM time regex 抽取(含邊界與 lookaround)
///   - ParseDuckDuckGoLite / ParseGoogleResults：HTML 解析、rank/url/title/snippet 塑形 + limit 截斷
///
/// 解析方法回傳 List&lt;object&gt; 的匿名物件(rank/title/url/snippet)、用反射讀屬性。
/// 這些是 web-search dispatch 路徑共用工具，解析回退會直接污染 agent 看到的搜尋結果。
/// </summary>
public class WebSearchHelperTests
{
    // 匿名物件屬性讀取
    private static object? Prop(object item, string name)
        => item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);

    private static string Str(object item, string name) => Prop(item, name)?.ToString() ?? string.Empty;
    private static int Rank(object item) => (int)(Prop(item, "rank") ?? -1);

    // ---------- ResolveWikipediaLanguages ----------

    [Fact]
    public void ResolveWikipediaLanguages_ZhLocale_PrefersZhThenEn()
    {
        WebSearchHelper.ResolveWikipediaLanguages("zh-TW")
            .Should().Equal("zh", "en");
    }

    [Fact]
    public void ResolveWikipediaLanguages_ZhPrefix_IsCaseInsensitive()
    {
        // StartsWith("zh", OrdinalIgnoreCase) → 大寫也算 zh
        WebSearchHelper.ResolveWikipediaLanguages("ZH")
            .Should().Equal("zh", "en");
    }

    [Fact]
    public void ResolveWikipediaLanguages_EnLocale_PrefersEnThenZh()
    {
        WebSearchHelper.ResolveWikipediaLanguages("en-US")
            .Should().Equal("en", "zh");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveWikipediaLanguages_BlankLocale_DefaultsEnThenZh(string? locale)
    {
        WebSearchHelper.ResolveWikipediaLanguages(locale!)
            .Should().Equal("en", "zh");
    }

    [Fact]
    public void ResolveWikipediaLanguages_NonZhNonBlank_DefaultsEnThenZh()
    {
        // 任何非 zh 開頭(且非空白)的 locale 都走預設 en→zh
        WebSearchHelper.ResolveWikipediaLanguages("ja")
            .Should().Equal("en", "zh");
    }

    // ---------- NormalizeWikipediaExtract ----------

    [Fact]
    public void NormalizeWikipediaExtract_CollapsesWhitespaceRuns()
    {
        WebSearchHelper.NormalizeWikipediaExtract("a   b\t\tc")
            .Should().Be("a b c");
    }

    [Fact]
    public void NormalizeWikipediaExtract_CollapsesNewlinesAndTrimsEnds()
    {
        // \s+ 涵蓋換行；前後空白應被 Trim()
        WebSearchHelper.NormalizeWikipediaExtract("  hello\n\nworld  ")
            .Should().Be("hello world");
    }

    [Fact]
    public void NormalizeWikipediaExtract_Null_ReturnsEmpty()
    {
        WebSearchHelper.NormalizeWikipediaExtract(null!)
            .Should().BeEmpty();
    }

    [Fact]
    public void NormalizeWikipediaExtract_AllWhitespace_ReturnsEmpty()
    {
        WebSearchHelper.NormalizeWikipediaExtract(" \t\n ")
            .Should().BeEmpty();
    }

    // ---------- ShortenText ----------

    [Fact]
    public void ShortenText_ShorterThanMax_ReturnedUnchanged()
    {
        WebSearchHelper.ShortenText("abc", 10).Should().Be("abc");
    }

    [Fact]
    public void ShortenText_ExactlyMax_ReturnedUnchanged()
    {
        // text.Length <= maxLength 走原樣分支(== 邊界)
        WebSearchHelper.ShortenText("abcde", 5).Should().Be("abcde");
    }

    [Fact]
    public void ShortenText_LongerThanMax_TruncatesAndAppendsEllipsis()
    {
        // "abcdefghij" 取前 5 = "abcde"、無尾隨空白、+ …
        WebSearchHelper.ShortenText("abcdefghij", 5)
            .Should().Be("abcde…");
    }

    [Fact]
    public void ShortenText_TruncationBoundaryTrimsTrailingWhitespace()
    {
        // 前 maxLength=4 字元 = "ab c"... 取 "ab  "(含尾空白)應 TrimEnd 後接省略號
        // 輸入 "ab    cd"：前 4 字元 = "ab  " → TrimEnd → "ab" + …
        WebSearchHelper.ShortenText("ab    cd", 4)
            .Should().Be("ab…");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ShortenText_BlankInput_ReturnedUnchanged(string? text)
    {
        // IsNullOrWhiteSpace 短路、不截斷、不接省略號
        WebSearchHelper.ShortenText(text!, 2).Should().Be(text);
    }

    // ---------- HtmlToText ----------

    [Fact]
    public void HtmlToText_StripsTags_AndTrims()
    {
        WebSearchHelper.HtmlToText("<p>Hello <b>world</b></p>")
            .Should().Be("Hello world");
    }

    [Fact]
    public void HtmlToText_RemovesScriptAndStyleContent()
    {
        var html = "before<script>var x = 1;</script>after<style>.a{color:red}</style>end";
        WebSearchHelper.HtmlToText(html)
            .Should().Be("beforeafterend");
    }

    [Fact]
    public void HtmlToText_DecodesHtmlEntities()
    {
        WebSearchHelper.HtmlToText("Tom &amp; Jerry &lt;3 &gt; &quot;x&quot;")
            .Should().Be("Tom & Jerry <3 > \"x\"");
    }

    [Fact]
    public void HtmlToText_BlockClosingTags_BecomeNewlines()
    {
        // </p> 與 <br> 各轉一個換行 → 兩個連續換行(未達 3 個、不壓縮)；開頭 <p> 被去標籤移除
        WebSearchHelper.HtmlToText("<p>line1</p><br>line2")
            .Should().Be("line1\n\nline2");
    }

    [Fact]
    public void HtmlToText_CompressesSpacesAndTabs()
    {
        WebSearchHelper.HtmlToText("a    b\t\tc")
            .Should().Be("a b c");
    }

    [Fact]
    public void HtmlToText_CollapsesThreeOrMoreNewlinesToTwo()
    {
        // <br> 各轉一個 \n、四個連續 → \n{4} → 壓成 \n\n
        WebSearchHelper.HtmlToText("a<br><br><br><br>b")
            .Should().Be("a\n\nb");
    }

    // ---------- ExtractTimeCandidates ----------

    [Fact]
    public void ExtractTimeCandidates_FindsBasicTimes()
    {
        WebSearchHelper.ExtractTimeCandidates("會議 09:30 與 14:05 開始")
            .Should().Equal("09:30", "14:05");
    }

    [Fact]
    public void ExtractTimeCandidates_Accepts23_59_Boundary()
    {
        WebSearchHelper.ExtractTimeCandidates("末班 23:59")
            .Should().Equal("23:59");
    }

    [Fact]
    public void ExtractTimeCandidates_Rejects24OrInvalidMinute()
    {
        // 24:00 小時超界、12:60 分鐘超界 → 皆不該匹配
        WebSearchHelper.ExtractTimeCandidates("24:00 12:60")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractTimeCandidates_LookaroundRejectsDigitAdjacency()
    {
        // 前後若緊鄰數字(如版本號/長串) lookbehind/lookahead 應排除
        WebSearchHelper.ExtractTimeCandidates("1209:301 0:00")
            .Should().Equal("0:00");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExtractTimeCandidates_Blank_YieldsNothing(string? text)
    {
        WebSearchHelper.ExtractTimeCandidates(text!).Should().BeEmpty();
    }

    [Fact]
    public void ExtractTimeCandidates_NoTimes_YieldsNothing()
    {
        WebSearchHelper.ExtractTimeCandidates("沒有任何時間在這裡").Should().BeEmpty();
    }

    // ---------- ParseGoogleResults ----------

    [Fact]
    public void ParseGoogleResults_ExtractsLinkTitleAndShapesRank()
    {
        var html = """<a href="/url?q=https://example.com/page&sa=U"><h3>Example Title</h3></a>""";

        var results = WebSearchHelper.ParseGoogleResults(html, 5);

        results.Should().HaveCount(1);
        Rank(results[0]).Should().Be(1);
        Str(results[0], "title").Should().Be("Example Title");
        Str(results[0], "url").Should().Be("https://example.com/page");
        Str(results[0], "snippet").Should().BeEmpty("Google 解析不填 snippet");
    }

    [Fact]
    public void ParseGoogleResults_SkipsNonHttpScheme()
    {
        // /url?q= 後面是非 http(s) → Uri 驗證後跳過
        var html = """<a href="/url?q=javascript:alert(1)"><h3>bad</h3></a>""";

        WebSearchHelper.ParseGoogleResults(html, 5).Should().BeEmpty();
    }

    [Fact]
    public void ParseGoogleResults_RespectsLimit()
    {
        var html =
            """<a href="/url?q=https://a.com"><h3>A</h3></a>""" +
            """<a href="/url?q=https://b.com"><h3>B</h3></a>""" +
            """<a href="/url?q=https://c.com"><h3>C</h3></a>""";

        var results = WebSearchHelper.ParseGoogleResults(html, 2);

        results.Should().HaveCount(2);
        Str(results[0], "title").Should().Be("A");
        Str(results[1], "title").Should().Be("B");
        Rank(results[1]).Should().Be(2);
    }

    [Fact]
    public void ParseGoogleResults_NoH3_FallsBackToHrefAsTitle()
    {
        // anchor 內無 <h3> 且 anchor 文字為空白 → title 回退成 href
        var html = """<a href="/url?q=https://example.org">   </a>""";

        var results = WebSearchHelper.ParseGoogleResults(html, 5);

        results.Should().HaveCount(1);
        Str(results[0], "title").Should().Be("https://example.org");
        Str(results[0], "url").Should().Be("https://example.org");
    }

    [Fact]
    public void ParseGoogleResults_NoMatches_ReturnsEmpty()
    {
        WebSearchHelper.ParseGoogleResults("<html><body>nothing</body></html>", 5)
            .Should().BeEmpty();
    }

    // ---------- ParseDuckDuckGoLite ----------

    [Fact]
    public void ParseDuckDuckGoLite_ExtractsLinkTitleAndSnippet()
    {
        var html =
            """<a class="result__a" href="https://example.com/x">Title One</a>""" +
            """<a class="result__snippet">Snippet body here</a>""";

        var results = WebSearchHelper.ParseDuckDuckGoLite(html, 5);

        results.Should().HaveCount(1);
        Rank(results[0]).Should().Be(1);
        Str(results[0], "title").Should().Be("Title One");
        Str(results[0], "url").Should().Be("https://example.com/x");
        Str(results[0], "snippet").Should().Be("Snippet body here");
    }

    [Fact]
    public void ParseDuckDuckGoLite_DecodesUddgRedirect()
    {
        // href 含 uddg= 重定向參數 → 應解出真實 URL
        var real = "https://example.com/real?a=1";
        var encoded = Uri.EscapeDataString(real);
        var html =
            $"""<a class="result__a" href="//duckduckgo.com/l/?uddg={encoded}&rut=abc">Redir</a>""";

        var results = WebSearchHelper.ParseDuckDuckGoLite(html, 5);

        results.Should().HaveCount(1);
        Str(results[0], "url").Should().Be(real);
    }

    [Fact]
    public void ParseDuckDuckGoLite_RespectsLimit()
    {
        var html =
            """<a class="result__a" href="https://a.com">A</a>""" +
            """<a class="result__a" href="https://b.com">B</a>""" +
            """<a class="result__a" href="https://c.com">C</a>""";

        var results = WebSearchHelper.ParseDuckDuckGoLite(html, 2);

        results.Should().HaveCount(2);
        Str(results[0], "title").Should().Be("A");
        Str(results[1], "title").Should().Be("B");
    }

    [Fact]
    public void ParseDuckDuckGoLite_MissingSnippet_DefaultsEmpty()
    {
        // 有連結但無對應 snippet 標籤 → snippet 為空字串、不報錯
        var html = """<a class="result__a" href="https://example.com">Only Link</a>""";

        var results = WebSearchHelper.ParseDuckDuckGoLite(html, 5);

        results.Should().HaveCount(1);
        Str(results[0], "snippet").Should().BeEmpty();
        Str(results[0], "title").Should().Be("Only Link");
    }

    [Fact]
    public void ParseDuckDuckGoLite_NoMatches_ReturnsEmpty()
    {
        WebSearchHelper.ParseDuckDuckGoLite("<html>nope</html>", 5)
            .Should().BeEmpty();
    }
}
