using System.Text.Json;
using SiteCrawlerWorker.Models;
using SiteCrawlerWorker.Services;

namespace Unit.Tests.Workers.SiteCrawler;

public class TemplateFrameworkLoaderTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"b4a-template-framework-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadDefault_LoadsBundledVisualPatternTemplates()
    {
        var loader = new TemplateFrameworkLoader();

        var templates = loader.LoadDefault();

        templates.Templates.Select(template => template.TemplateId).Should().Contain([
            "hero_news_portal",
            "search_service_portal",
            "service_action_portal",
            "search_results_portal",
            "report_dashboard",
            "input_flow",
            "commercial_showcase",
        ]);
        var heroNews = templates.Templates.Single(template => template.TemplateId == "hero_news_portal");
        heroNews.PatternTags.Should().Contain(["hero", "news", "quick_links"]);
        heroNews.SupportedSiteKinds.Should().BeEmpty();
        heroNews.PageTypes.Should().ContainKey("home");
        heroNews.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "hero" &&
            slot.Accepts.Contains("HeroCarousel") &&
            slot.Accepts.Contains("HeroBanner") &&
            slot.Accepts.Contains("AtomicSection"));
        heroNews.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "news" &&
            slot.Accepts.Contains("NewsCardCarousel") &&
            slot.Accepts.Contains("NewsGrid"));
        var searchService = templates.Templates.Single(template => template.TemplateId == "search_service_portal");
        searchService.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "hero" &&
            slot.Accepts.Contains("HeroCarousel"));
        searchService.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "search" &&
            slot.Accepts.Contains("ServiceSearchHero"));
        searchService.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "service_categories" &&
            slot.Accepts.Contains("ServiceCategoryGrid"));
        searchService.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "news" &&
            slot.Accepts.Contains("NewsGrid"));
        var serviceAction = templates.Templates.Single(template => template.TemplateId == "service_action_portal");
        serviceAction.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "news" &&
            slot.Accepts.Contains("NewsGrid"));
        serviceAction.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "service_actions" &&
            slot.Accepts.Contains("ServiceActionGrid"));
        serviceAction.PageTypes["home"].Slots.Should().Contain(slot =>
            slot.Name == "tabbed_news" &&
            slot.Accepts.Contains("TabbedNewsBoard"));

        var searchResults = templates.Templates.Single(template => template.TemplateId == "search_results_portal");
        searchResults.PatternTags.Should().Contain(["search_results", "filters", "results"]);
        searchResults.PageTypes["home"].Slots.Select(slot => slot.Name).Should().Contain([
            "search_box",
            "filter_panel",
            "result_list",
            "pagination",
        ]);
        searchResults.PageTypes["home"].Slots.Single(slot => slot.Name == "search_box").Accepts.Should().Contain("SearchBoxPanel");
        searchResults.PageTypes["home"].Slots.Single(slot => slot.Name == "filter_panel").Accepts.Should().Contain("FacetFilterPanel");
        searchResults.PageTypes["home"].Slots.Single(slot => slot.Name == "result_list").Accepts.Should().Contain("ResultList");
        searchResults.PageTypes["home"].Slots.Single(slot => slot.Name == "pagination").Accepts.Should().Contain("PaginationNav");

        var reportDashboard = templates.Templates.Single(template => template.TemplateId == "report_dashboard");
        reportDashboard.PatternTags.Should().Contain(["report", "metrics", "data_table"]);
        reportDashboard.PageTypes["home"].Slots.Select(slot => slot.Name).Should().Contain([
            "filter_bar",
            "metric_summary",
            "chart_panel",
            "data_table",
        ]);
        reportDashboard.PageTypes["home"].Slots.Single(slot => slot.Name == "filter_bar").Accepts.Should().Contain("DashboardFilterBar");
        reportDashboard.PageTypes["home"].Slots.Single(slot => slot.Name == "metric_summary").Accepts.Should().Contain("MetricSummaryGrid");
        reportDashboard.PageTypes["home"].Slots.Single(slot => slot.Name == "chart_panel").Accepts.Should().Contain("ChartPanel");
        reportDashboard.PageTypes["home"].Slots.Single(slot => slot.Name == "data_table").Accepts.Should().Contain("DataTablePreview");

        var inputFlow = templates.Templates.Single(template => template.TemplateId == "input_flow");
        inputFlow.PatternTags.Should().Contain(["input_flow", "form", "steps"]);
        inputFlow.PageTypes["home"].Slots.Select(slot => slot.Name).Should().Contain([
            "step_indicator",
            "form_fields",
            "validation_summary",
            "action_bar",
        ]);
        inputFlow.PageTypes["home"].Slots.Single(slot => slot.Name == "step_indicator").Accepts.Should().Contain("StepIndicator");
        inputFlow.PageTypes["home"].Slots.Single(slot => slot.Name == "form_fields").Accepts.Should().Contain("StructuredFormPanel");
        inputFlow.PageTypes["home"].Slots.Single(slot => slot.Name == "validation_summary").Accepts.Should().Contain("ValidationSummary");
        inputFlow.PageTypes["home"].Slots.Single(slot => slot.Name == "action_bar").Accepts.Should().Contain("FormActionBar");

        var showcase = templates.Templates.Single(template => template.TemplateId == "commercial_showcase");
        showcase.PatternTags.Should().Contain(["showcase", "product_cards", "cta"]);
        showcase.PageTypes["home"].Slots.Select(slot => slot.Name).Should().Contain([
            "showcase_hero",
            "product_cards",
            "proof_strip",
            "pricing_panel",
            "cta_band",
        ]);
        showcase.PageTypes["home"].Slots.Single(slot => slot.Name == "showcase_hero").Accepts.Should().Contain("ShowcaseHero");
        showcase.PageTypes["home"].Slots.Single(slot => slot.Name == "product_cards").Accepts.Should().Contain("ProductCardGrid");
        showcase.PageTypes["home"].Slots.Single(slot => slot.Name == "proof_strip").Accepts.Should().Contain("ProofStrip");
        showcase.PageTypes["home"].Slots.Single(slot => slot.Name == "pricing_panel").Accepts.Should().Contain("PricingPanel");
        showcase.PageTypes["home"].Slots.Single(slot => slot.Name == "cta_band").Accepts.Should().Contain("CtaBand");
    }

    [Fact]
    public void LoadDefault_TemplateFrameworkReadmeDocumentsEveryTemplate()
    {
        var templates = new TemplateFrameworkLoader().LoadDefault();
        var readme = File.ReadAllText(ResolveRepoFile(
            "packages",
            "csharp",
            "workers",
            "site-crawler-worker",
            "template-framework",
            "README.html"));

        foreach (var template in templates.Templates)
        {
            // [修正 2026-10 AnthonyLee] README 已改為產生後的 HTML，markdown 的 `x` 會轉成 <code>x</code>，
            // 原本找反引號在 HTML 裡永遠找不到。
            readme.Should().Contain($"<code>{template.TemplateId}</code>");
        }
    }

    [Fact]
    public void Load_LoadsExternalTemplateFile()
    {
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "custom_template.json");
        var manifest = new TemplateFrameworkManifest
        {
            Templates =
            [
                new TemplateDefinition
                {
                    TemplateId = "custom_site",
                    SupportedSiteKinds = ["custom"],
                    PageTypes =
                    {
                        ["home"] = new TemplatePageTypeDefinition
                        {
                            Slots =
                            [
                                new TemplateSlotDefinition
                                {
                                    Name = "content",
                                    Required = true,
                                    Accepts = ["ContentSection"],
                                    Fallback = "ContentSection",
                                },
                            ],
                        },
                    },
                },
            ],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var loader = new TemplateFrameworkLoader();

        var loaded = loader.Load(path);

        loaded.Templates.Should().ContainSingle(template => template.TemplateId == "custom_site");
        loaded.Templates[0].PageTypes["home"].Slots[0].Accepts.Should().Contain("ContentSection");
    }

    [Fact]
    public void Load_WhenSlotHasNoAcceptedComponents_Throws()
    {
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "broken_template.json");
        File.WriteAllText(path, """
            {
              "templates": [
                {
                  "template_id": "broken",
                  "supported_site_kinds": ["custom"],
                  "page_types": {
                    "home": {
                      "slots": [
                        { "name": "content", "required": true, "accepts": [] }
                      ]
                    }
                  }
                }
              ]
            }
            """);
        var loader = new TemplateFrameworkLoader();

        var act = () => loader.Load(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*accepted component*");
    }

    private static string ResolveRepoFile(params string[] segments)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repo file: {Path.Combine(segments)}");
    }
}
