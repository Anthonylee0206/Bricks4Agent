using System.Text.Json;
using SiteCrawlerWorker.Models;
using SiteCrawlerWorker.Services;

namespace Unit.Tests.Workers.SiteCrawler;

public class ComponentLibraryLoaderTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"b4a-component-library-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadDefault_LoadsBundledDefaultManifest()
    {
        var loader = new ComponentLibraryLoader();

        var manifest = loader.LoadDefault();

        manifest.LibraryId.Should().Be("bricks4agent.default");
        manifest.Components.Should().Contain(component => component.Type == "PageShell");
        manifest.Components.Select(component => component.Type).Should().Contain([
            "MegaHeader",
            "HeroCarousel",
            "HeroBanner",
            "QuickLinkRibbon",
            "NewsCardCarousel",
            "NewsGrid",
            "MediaFeatureGrid",
            "ServiceSearchHero",
            "ServiceCategoryGrid",
            "ServiceActionGrid",
            "TabbedNewsBoard",
            "SearchBoxPanel",
            "FacetFilterPanel",
            "ResultList",
            "PaginationNav",
            "DashboardFilterBar",
            "MetricSummaryGrid",
            "ChartPanel",
            "DataTablePreview",
            "StepIndicator",
            "StructuredFormPanel",
            "ValidationSummary",
            "FormActionBar",
            "ShowcaseHero",
            "ProductCardGrid",
            "ProofStrip",
            "PricingPanel",
            "CtaBand",
            "InstitutionFooter",
            "ArticleList",
            "ContentArticle",
        ]);
        manifest.Components.Should().NotContain(component => component.Generated);
        manifest.Components.Should().OnlyContain(component => component.PropsSchema.Properties.Count > 0);
    }

    [Fact]
    public void LoadDefault_ComponentLibraryReadmeDocumentsEveryManifestComponent()
    {
        var manifest = new ComponentLibraryLoader().LoadDefault();
        var readme = File.ReadAllText(ResolveRepoFile(
            "packages",
            "csharp",
            "workers",
            "site-crawler-worker",
            "component-libraries",
            "bricks4agent.default",
            "README.html"));

        foreach (var component in manifest.Components)
        {
            // [修正 2026-10 AnthonyLee] README 已改為產生後的 HTML，markdown 的 `x` 會轉成 <code>x</code>，
            // 原本找反引號在 HTML 裡永遠找不到。
            readme.Should().Contain($"<code>{component.Type}</code>");
        }
    }

    [Fact]
    public void Load_LoadsExternalManifest()
    {
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "manifest.json");
        var manifest = DefaultComponentLibrary.Create();
        manifest.LibraryId = "custom.library";
        manifest.Components.Add(DefaultComponentLibrary.Define(
            "CustomNotice",
            "Custom notice component.",
            ["notice"],
            new ComponentPropsSchema
            {
                Required = ["title"],
                Properties = { ["title"] = new ComponentPropSchema { Type = "string" } },
            },
            bComponent: "Alert"));
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var loader = new ComponentLibraryLoader();

        var loaded = loader.Load(path);

        loaded.LibraryId.Should().Be("custom.library");
        loaded.Components.Should().Contain(component => component.Type == "CustomNotice");
    }

    [Fact]
    public void Load_WhenPathIsDirectory_LoadsManifestInsideDirectory()
    {
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(DefaultComponentLibrary.Create(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var loader = new ComponentLibraryLoader();

        var loaded = loader.Load(tempRoot);

        loaded.LibraryId.Should().Be("bricks4agent.default");
        loaded.Components.Should().Contain(component => component.Type == "PageShell");
    }

    [Fact]
    public void Load_WhenManifestOmitsPropsSchema_Throws()
    {
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "manifest.json");
        File.WriteAllText(path, """
            {
              "library_id": "broken",
              "version": "1.0.0",
              "components": [
                {
                  "type": "BrokenComponent",
                  "description": "Broken",
                  "supported_roles": ["broken"]
                }
              ]
            }
            """);
        var loader = new ComponentLibraryLoader();

        var act = () => loader.Load(path);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*props_schema*");
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
