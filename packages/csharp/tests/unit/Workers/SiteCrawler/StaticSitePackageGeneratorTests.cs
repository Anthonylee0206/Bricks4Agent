using System.Text.Json;
using System.IO.Compression;
using SiteCrawlerWorker.Models;
using SiteCrawlerWorker.Services;

namespace Unit.Tests.Workers.SiteCrawler;

public class StaticSitePackageGeneratorTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"b4a-package-test-{Guid.NewGuid():N}");
    // 與 StaticSitePackageGenerator 的標記檔名稱一致（產生器以它辨識自己產生過的目錄）
    private const string PackageMarkerFileName = ".bricks4agent-static-site-package";

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Generate_WritesBComponentBindingAnchoredToUiComponents()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props = { ["title"] = "Example", ["source_url"] = "https://example.com/" },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "content",
                                Type = "ContentSection",
                                Props = { ["title"] = "Example", ["body"] = "Welcome." },
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var first = generator.Generate(document, new StaticSitePackageOptions { OutputDirectory = tempRoot, PackageName = "bind-a" });
        var second = generator.Generate(document, new StaticSitePackageOptions { OutputDirectory = tempRoot, PackageName = "bind-b" });

        var bindingPath = Path.Combine(first.OutputDirectory, "components", "b-binding.json");
        File.Exists(bindingPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(bindingPath));
        doc.RootElement.GetProperty("library").GetString().Should().Be("ui_components");
        var bindings = doc.RootElement.GetProperty("bindings");
        // The collapse is verifiable: A's hero jargon family resolves to one B section; the tabbed
        // board resolves to B's TabContainer; every binding is a real B class.
        bindings.GetProperty("HeroCarousel").GetString().Should().Be("BannerSection");
        bindings.GetProperty("ShowcaseHero").GetString().Should().Be("BannerSection");
        bindings.GetProperty("TabbedNewsBoard").GetString().Should().Be("TabContainer");
        bindings.GetProperty("ContentSection").GetString().Should().Be("ContentSection");
        foreach (var binding in bindings.EnumerateObject())
        {
            BComponentRegistry.Contains(binding.Value.GetString()).Should().BeTrue();
        }

        // Deterministic across runs.
        File.ReadAllText(bindingPath).Should().Be(
            File.ReadAllText(Path.Combine(second.OutputDirectory, "components", "b-binding.json")));

        File.ReadAllText(Path.Combine(first.OutputDirectory, "README.html"))
            .Should().Contain("ui_components").And.Contain("b-binding.json");
    }

    [Fact]
    public void Generate_WritesRuntimePackageThatLoadsSiteJson()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "hero",
                                Type = "ContentSection",
                                Props =
                                {
                                    ["title"] = "Example",
                                    ["body"] = "Welcome.",
                                },
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "example-site",
        });

        File.Exists(Path.Combine(result.OutputDirectory, "index.html")).Should().BeTrue();
        File.Exists(Path.Combine(result.OutputDirectory, "runtime.js")).Should().BeTrue();
        File.Exists(Path.Combine(result.OutputDirectory, "styles.css")).Should().BeTrue();
        File.Exists(Path.Combine(result.OutputDirectory, "site.json")).Should().BeTrue();
        File.Exists(Path.Combine(result.OutputDirectory, "components", "manifest.json")).Should().BeTrue();

        var index = File.ReadAllText(Path.Combine(result.OutputDirectory, "index.html"));
        index.Should().Contain("<div id=\"app\"></div>");
        index.Should().Contain("./runtime.js");
        index.Should().NotContain("Welcome.");

        var runtime = File.ReadAllText(Path.Combine(result.OutputDirectory, "runtime.js"));
        runtime.Should().Contain("fetch('./site.json')");
        runtime.Should().Contain("componentRenderers");
        runtime.Should().Contain("navigateToRoute");
        runtime.Should().Contain("routePathFromLocation");
        runtime.Should().Contain("window.location.hash");
        runtime.Should().Contain("data-local-route");
        runtime.Should().Contain("renderAtomicSection");
        runtime.Should().Contain("renderFeatureCard");
        runtime.Should().Contain("renderLinkSet");
        runtime.Should().Contain("card-grid--carousel");
        runtime.Should().Contain("normalizeInternalRoute");
        runtime.Should().Contain("toStaticHref");

        var siteJson = JsonSerializer.Deserialize<GeneratorSiteDocument>(
            File.ReadAllText(Path.Combine(result.OutputDirectory, "site.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        siteJson.Should().NotBeNull();
        siteJson!.Routes.Should().ContainSingle();
        result.EntryPoint.Should().Be(Path.Combine(result.OutputDirectory, "index.html"));
    }

    [Fact]
    public void Generate_HeroCarouselRuntimeRendersImageFirstVisualBanner()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "hero",
                                Type = "HeroCarousel",
                                Props =
                                {
                                    ["title"] = "",
                                    ["body"] = "",
                                    ["slides"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["title"] = "",
                                            ["body"] = "",
                                            ["media_url"] = "https://example.com/hero.jpg",
                                            ["media_alt"] = "Hero",
                                            ["url"] = "",
                                            ["source_url"] = "",
                                            ["scope"] = "none",
                                        },
                                    },
                                },
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "hero-carousel-site",
        });

        var runtime = File.ReadAllText(Path.Combine(result.OutputDirectory, "runtime.js"));
        var styles = File.ReadAllText(Path.Combine(result.OutputDirectory, "styles.css"));
        runtime.Should().Contain("template-hero--visual-banner");
        runtime.Should().Contain("hasMeaningfulHeroText");
        styles.Should().Contain(".template-hero--visual-banner");
        styles.Should().Contain("grid-template-columns: 1fr");
        styles.Should().Contain(".template-hero--visual-banner .template-hero-media img");
        styles.Should().Contain("object-fit: contain");
    }

    [Fact]
    public void Generate_TemplateCardRuntimeLinksCardTitlesWithoutOpenPlaceholders()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "news",
                                Type = "NewsGrid",
                                Props =
                                {
                                    ["title"] = "News",
                                    ["items"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["title"] = "Campus Story",
                                            ["body"] = "Story body.",
                                            ["media_url"] = "https://example.com/story.jpg",
                                            ["media_alt"] = "Story",
                                            ["url"] = "/story",
                                            ["source_url"] = "https://example.com/story",
                                            ["scope"] = "internal",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "tabbed-news",
                                Type = "TabbedNewsBoard",
                                Props =
                                {
                                    ["title"] = "Announcements",
                                    ["tabs"] = new List<Dictionary<string, object?>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Latest",
                                            ["items"] = new List<Dictionary<string, string>>
                                            {
                                                new()
                                                {
                                                    ["title"] = "Enrollment update",
                                                    ["body"] = "Update body.",
                                                    ["url"] = "/announcements/1",
                                                    ["source_url"] = "https://example.com/announcements/1",
                                                    ["scope"] = "internal",
                                                    ["media_url"] = "",
                                                    ["media_alt"] = "",
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "news-card-site",
        });

        var runtime = File.ReadAllText(Path.Combine(result.OutputDirectory, "runtime.js"));
        var styles = File.ReadAllText(Path.Combine(result.OutputDirectory, "styles.css"));
        runtime.Should().Contain("template-card-title-link");
        runtime.Should().NotContain("const anchor = element('a', '', 'Open');");
        runtime.Should().NotContain("body.appendChild(anchor);");
        runtime.Should().NotContain("article.appendChild(anchor);");
        styles.Should().Contain(".template-card-title-link");
    }

    [Fact]
    public void Generate_RuntimeDoesNotNavigateToOriginalExternalUrls()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "links",
                                Type = "LinkList",
                                Props =
                                {
                                    ["title"] = "Links",
                                    ["links"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "External source",
                                            ["url"] = "https://source.example.edu/news.aspx?id=1",
                                            ["source_url"] = "https://source.example.edu/news.aspx?id=1",
                                            ["scope"] = "external",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "footer",
                                Type = "InstitutionFooter",
                                Props =
                                {
                                    ["source_url"] = "https://example.com/",
                                    ["contact_text"] = "Address",
                                    ["links"] = new List<Dictionary<string, string>>(),
                                },
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "external-link-site",
        });

        var runtime = File.ReadAllText(Path.Combine(result.OutputDirectory, "runtime.js"));
        runtime.Should().Contain("sourceRouteMap");
        runtime.Should().Contain("link.scope === 'external'");
        runtime.Should().Contain("anchor.removeAttribute('href');");
        runtime.Should().Contain("anchor.dataset.sourceUrl");
        runtime.Should().NotContain("source.href = node.props.source_url;");
        runtime.Should().NotContain("anchor.href = '#';");
    }

    [Fact]
    public void Generate_WhenDocumentHasGeneratedComponent_WritesLoadableComponentModule()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
        };
        document.ComponentLibrary.Components.Add(DefaultComponentLibrary.Define(
            "GeneratedNewsSection",
            "Generated news component.",
            ["news"],
            new ComponentPropsSchema
            {
                Required = ["title", "body"],
                Properties =
                {
                    ["title"] = new ComponentPropSchema { Type = "string" },
                    ["body"] = new ComponentPropSchema { Type = "string" },
                    ["source_selector"] = new ComponentPropSchema { Type = "string" },
                },
            },
            generated: true));
        document.Routes.Add(new GeneratorRoute
        {
            Path = "/",
            Title = "Example",
            SourceUrl = "https://example.com/",
            Root = new ComponentNode
            {
                Id = "page",
                Type = "PageShell",
                Props =
                {
                    ["title"] = "Example",
                    ["source_url"] = "https://example.com/",
                },
                Children =
                [
                    new ComponentNode
                    {
                        Id = "news",
                        Type = "GeneratedNewsSection",
                        Props =
                        {
                            ["title"] = "Campus News",
                            ["body"] = "Latest updates.",
                            ["source_selector"] = "section.news-list",
                        },
                    },
                ],
            },
        });
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "custom-component-site",
        });

        var modulePath = Path.Combine(result.OutputDirectory, "components", "generated", "GeneratedNewsSection.js");
        File.Exists(modulePath).Should().BeTrue();
        File.ReadAllText(modulePath).Should().Contain("export function render");
        File.ReadAllText(Path.Combine(result.OutputDirectory, "runtime.js")).Should().Contain("loadGeneratedRenderers");
        result.Files.Should().Contain(modulePath);
    }

    [Fact]
    public void Generate_WhenStrictQualityGateFails_DoesNotWritePackage()
    {
        var document = ComponentSchemaValidatorTests.BuildValidDocument();
        document.ComponentRequests.Add(new ComponentRequest
        {
            RequestId = "component-request-1",
            Role = "hero",
            ComponentType = "MissingHero",
            Reason = "preferred component missing",
        });
        var generator = new StaticSitePackageGenerator();

        var act = () => generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "strict-fail",
            EnforceQualityGate = true,
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*quality gate failed*component request*");
        Directory.Exists(Path.Combine(tempRoot, "strict-fail")).Should().BeFalse();
    }

    [Fact]
    public void Generate_WhenCreateArchiveIsEnabled_WritesPortableZipPackage()
    {
        var document = ComponentSchemaValidatorTests.BuildValidDocument();
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "archive-site",
            EnforceQualityGate = true,
            CreateArchive = true,
        });

        result.ArchivePath.Should().Be(Path.Combine(tempRoot, "archive-site.zip"));
        File.Exists(result.ArchivePath).Should().BeTrue();
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        archive.Entries.Select(entry => entry.FullName).Should().Contain([
            "index.html",
            "site.json",
            "components/manifest.json",
        ]);
    }

    [Fact]
    public void Generate_WhenPackageDirectoryAlreadyExists_RemovesStaleFilesBeforeWriting()
    {
        var staleDirectory = Path.Combine(tempRoot, "clean-site");
        Directory.CreateDirectory(Path.Combine(staleDirectory, "components", "generated"));
        // [修正 2026-10 AnthonyLee] 產生器只清掉「自己產生過」的目錄（以標記檔辨識），這裡模擬上一次的產出
        File.WriteAllText(Path.Combine(staleDirectory, PackageMarkerFileName), "previous package");
        File.WriteAllText(Path.Combine(staleDirectory, "old.txt"), "stale");
        File.WriteAllText(Path.Combine(staleDirectory, "components", "generated", "OldComponent.js"), "stale");
        var document = ComponentSchemaValidatorTests.BuildValidDocument();
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "clean-site",
            EnforceQualityGate = true,
            CreateArchive = true,
        });

        File.Exists(Path.Combine(result.OutputDirectory, "old.txt")).Should().BeFalse();
        File.Exists(Path.Combine(result.OutputDirectory, "components", "generated", "OldComponent.js")).Should().BeFalse();
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        archive.Entries.Select(entry => entry.FullName).Should().NotContain([
            "old.txt",
            "components/generated/OldComponent.js",
        ]);
    }

    [Fact]
    public void Generate_WhenDirectoryIsNotAPackage_RefusesAndKeepsItsFiles()
    {
        // [新增 2026-10 AnthonyLee] 防止打錯路徑時遞迴刪掉不相干的目錄：沒有標記檔的非空目錄要拒絕、內容不能動
        var unrelatedDirectory = Path.Combine(tempRoot, "not-a-package");
        Directory.CreateDirectory(unrelatedDirectory);
        var keep = Path.Combine(unrelatedDirectory, "keep.txt");
        File.WriteAllText(keep, "user data");
        var generator = new StaticSitePackageGenerator();

        var act = () => generator.Generate(ComponentSchemaValidatorTests.BuildValidDocument(), new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "not-a-package",
            EnforceQualityGate = true,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to overwrite*");
        File.ReadAllText(keep).Should().Be("user data");
    }

    [Fact]
    public void Generate_WritesCompactStandardImageStylesAndHeroOnlyLargeImageStyles()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    SourceUrl = "https://example.com/",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "notice",
                                Type = "AtomicSection",
                                Props =
                                {
                                    ["variant"] = "standard",
                                    ["source_selector"] = "div.reminder",
                                },
                                Children =
                                [
                                    new ComponentNode
                                    {
                                        Id = "notice-image",
                                        Type = "ImageBlock",
                                        Props =
                                        {
                                            ["url"] = "https://example.com/reminder.png",
                                            ["alt"] = "Reminder",
                                        },
                                    },
                                ],
                            },
                            new ComponentNode
                            {
                                Id = "hero",
                                Type = "AtomicSection",
                                Props =
                                {
                                    ["variant"] = "hero",
                                    ["source_selector"] = "section.hero",
                                },
                                Children =
                                [
                                    new ComponentNode
                                    {
                                        Id = "hero-image",
                                        Type = "ImageBlock",
                                        Props =
                                        {
                                            ["url"] = "https://example.com/hero.jpg",
                                            ["alt"] = "Campus",
                                        },
                                    },
                                ],
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "image-style-site",
        });

        var styles = File.ReadAllText(Path.Combine(result.OutputDirectory, "styles.css"));
        styles.Should().Contain(".atomic-section--standard .image-block");
        styles.Should().Contain("width: min(260px, 100%)");
        styles.Should().Contain("max-height: 160px");
        styles.Should().Contain(".atomic-section--hero .image-block");
        styles.Should().Contain("min-height: 260px");
        styles.Should().Contain("object-fit: cover");
        styles.Should().Contain(".content-article-media {");
        styles.Should().Contain("grid-template-columns: repeat(auto-fill, minmax(180px, 280px))");
        styles.Should().Contain("max-height: 180px");
    }

    [Fact]
    public void Generate_WritesRuntimeRenderersForTemplateComponents()
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    SourceUrl = "https://example.com/",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "header",
                                Type = "MegaHeader",
                                Props =
                                {
                                    ["title"] = "Example",
                                    ["logo_url"] = "",
                                    ["logo_alt"] = "",
                                    ["utility_links"] = new List<Dictionary<string, string>>(),
                                    ["primary_links"] = new List<Dictionary<string, string>>(),
                                    ["search_enabled"] = true,
                                },
                            },
                            new ComponentNode
                            {
                                Id = "hero",
                                Type = "HeroCarousel",
                                Props =
                                {
                                    ["title"] = "Campus",
                                    ["body"] = "Welcome.",
                                    ["slides"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["title"] = "Slide",
                                            ["body"] = "Slide body",
                                            ["media_url"] = "https://example.com/hero.jpg",
                                            ["media_alt"] = "Hero",
                                            ["url"] = "/about",
                                            ["source_url"] = "https://example.com/about",
                                            ["scope"] = "internal",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "quick",
                                Type = "QuickLinkRibbon",
                                Props =
                                {
                                    ["title"] = "Quick Links",
                                    ["links"] = new List<Dictionary<string, string>>(),
                                },
                            },
                            new ComponentNode
                            {
                                Id = "news",
                                Type = "NewsCardCarousel",
                                Props =
                                {
                                    ["title"] = "News",
                                    ["items"] = new List<Dictionary<string, string>>(),
                                },
                            },
                            new ComponentNode
                            {
                                Id = "service-search",
                                Type = "ServiceSearchHero",
                                Props =
                                {
                                    ["title"] = "How can we help?",
                                    ["body"] = "Search services.",
                                    ["query_placeholder"] = "Search services",
                                    ["hot_keywords"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Tax filing",
                                            ["url"] = "/tax",
                                            ["source_url"] = "https://example.com/tax",
                                            ["scope"] = "internal",
                                        },
                                    },
                                    ["actions"] = new List<Dictionary<string, string>>(),
                                },
                            },
                            new ComponentNode
                            {
                                Id = "service-categories",
                                Type = "ServiceCategoryGrid",
                                Props =
                                {
                                    ["title"] = "Services",
                                    ["categories"] = new List<Dictionary<string, object?>>
                                    {
                                        new()
                                        {
                                            ["title"] = "Household",
                                            ["body"] = "Registration services.",
                                            ["links"] = new List<Dictionary<string, string>>
                                            {
                                                new()
                                                {
                                                    ["label"] = "Open",
                                                    ["url"] = "/household",
                                                    ["source_url"] = "https://example.com/household",
                                                    ["scope"] = "internal",
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "service-actions",
                                Type = "ServiceActionGrid",
                                Props =
                                {
                                    ["title"] = "Actions",
                                    ["actions"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Online registration",
                                            ["url"] = "/register",
                                            ["source_url"] = "https://example.com/register",
                                            ["scope"] = "internal",
                                            ["kind"] = "primary",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "tabbed-news",
                                Type = "TabbedNewsBoard",
                                Props =
                                {
                                    ["title"] = "Announcements",
                                    ["tabs"] = new List<Dictionary<string, object?>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Latest",
                                            ["items"] = new List<Dictionary<string, string>>
                                            {
                                                new()
                                                {
                                                    ["title"] = "Service update",
                                                    ["body"] = "2026-05-01",
                                                    ["url"] = "/news/1",
                                                    ["source_url"] = "https://example.com/news/1",
                                                    ["scope"] = "internal",
                                                    ["media_url"] = "",
                                                    ["media_alt"] = "",
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "search-box",
                                Type = "SearchBoxPanel",
                                Props =
                                {
                                    ["title"] = "Search",
                                    ["body"] = "Search all content.",
                                    ["query_placeholder"] = "Search keyword",
                                    ["suggestions"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Permit",
                                            ["url"] = "/search/permit",
                                            ["source_url"] = "https://example.com/search/permit",
                                            ["scope"] = "internal",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "filters",
                                Type = "FacetFilterPanel",
                                Props =
                                {
                                    ["title"] = "Filters",
                                    ["filters"] = new List<Dictionary<string, string>>
                                    {
                                        new() { ["label"] = "Type", ["value"] = "Article", ["count"] = "12" },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "results",
                                Type = "ResultList",
                                Props =
                                {
                                    ["title"] = "Results",
                                    ["summary"] = "12 results",
                                    ["items"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["title"] = "Result",
                                            ["body"] = "Result body",
                                            ["url"] = "/result",
                                            ["source_url"] = "https://example.com/result",
                                            ["scope"] = "internal",
                                            ["media_url"] = "",
                                            ["media_alt"] = "",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "pagination",
                                Type = "PaginationNav",
                                Props =
                                {
                                    ["links"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Next",
                                            ["url"] = "/search/page-2",
                                            ["source_url"] = "https://example.com/search?page=2",
                                            ["scope"] = "internal",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "dashboard-filter",
                                Type = "DashboardFilterBar",
                                Props =
                                {
                                    ["title"] = "Report filters",
                                    ["filters"] = new List<Dictionary<string, string>>
                                    {
                                        new() { ["label"] = "Date", ["value"] = "Last 30 days", ["count"] = "" },
                                    },
                                    ["actions"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Export",
                                            ["url"] = "/export",
                                            ["source_url"] = "https://example.com/export",
                                            ["scope"] = "internal",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "metrics",
                                Type = "MetricSummaryGrid",
                                Props =
                                {
                                    ["title"] = "Metrics",
                                    ["metrics"] = new List<Dictionary<string, string>>
                                    {
                                        new() { ["label"] = "Total", ["value"] = "120", ["detail"] = "This month" },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "chart",
                                Type = "ChartPanel",
                                Props =
                                {
                                    ["title"] = "Trend",
                                    ["body"] = "Monthly values",
                                    ["series"] = new List<Dictionary<string, string>>
                                    {
                                        new() { ["label"] = "Jan", ["value"] = "20" },
                                        new() { ["label"] = "Feb", ["value"] = "32" },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "table",
                                Type = "DataTablePreview",
                                Props =
                                {
                                    ["title"] = "Rows",
                                    ["columns"] = new List<string> { "Name", "Value" },
                                    ["rows"] = new List<Dictionary<string, object?>>
                                    {
                                        new() { ["cells"] = new List<string> { "A", "1" } },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "steps",
                                Type = "StepIndicator",
                                Props =
                                {
                                    ["steps"] = new List<Dictionary<string, string>>
                                    {
                                        new() { ["label"] = "Start", ["status"] = "current" },
                                        new() { ["label"] = "Review", ["status"] = "upcoming" },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "structured-form",
                                Type = "StructuredFormPanel",
                                Props =
                                {
                                    ["title"] = "Form",
                                    ["fields"] = new List<Dictionary<string, object?>>
                                    {
                                        new()
                                        {
                                            ["name"] = "email",
                                            ["id"] = "email",
                                            ["label"] = "Email",
                                            ["type"] = "email",
                                            ["required"] = true,
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "validation",
                                Type = "ValidationSummary",
                                Props =
                                {
                                    ["title"] = "Required fields",
                                    ["messages"] = new List<string> { "Email is required." },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "form-actions",
                                Type = "FormActionBar",
                                Props =
                                {
                                    ["actions"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Continue",
                                            ["url"] = "/continue",
                                            ["source_url"] = "https://example.com/continue",
                                            ["scope"] = "internal",
                                            ["kind"] = "primary",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "showcase",
                                Type = "ShowcaseHero",
                                Props =
                                {
                                    ["title"] = "Product",
                                    ["body"] = "Product value.",
                                    ["media_url"] = "https://example.com/product.jpg",
                                    ["media_alt"] = "Product",
                                    ["actions"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Start",
                                            ["url"] = "/start",
                                            ["source_url"] = "https://example.com/start",
                                            ["scope"] = "internal",
                                            ["kind"] = "primary",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "product-cards",
                                Type = "ProductCardGrid",
                                Props =
                                {
                                    ["title"] = "Products",
                                    ["items"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["title"] = "Starter",
                                            ["body"] = "Starter plan",
                                            ["url"] = "/starter",
                                            ["source_url"] = "https://example.com/starter",
                                            ["scope"] = "internal",
                                            ["media_url"] = "",
                                            ["media_alt"] = "",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "proof",
                                Type = "ProofStrip",
                                Props =
                                {
                                    ["title"] = "Proof",
                                    ["items"] = new List<Dictionary<string, string>>
                                    {
                                        new() { ["label"] = "Users", ["value"] = "500", ["detail"] = "Teams" },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "pricing",
                                Type = "PricingPanel",
                                Props =
                                {
                                    ["title"] = "Pricing",
                                    ["plans"] = new List<Dictionary<string, object?>>
                                    {
                                        new()
                                        {
                                            ["title"] = "Starter",
                                            ["price"] = "$19",
                                            ["body"] = "For small teams",
                                            ["features"] = new List<string> { "Feature A" },
                                            ["action"] = new Dictionary<string, string>
                                            {
                                                ["label"] = "Buy",
                                                ["url"] = "/buy",
                                                ["source_url"] = "https://example.com/buy",
                                                ["scope"] = "internal",
                                                ["kind"] = "primary",
                                            },
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "cta",
                                Type = "CtaBand",
                                Props =
                                {
                                    ["title"] = "Ready?",
                                    ["body"] = "Start today.",
                                    ["actions"] = new List<Dictionary<string, string>>
                                    {
                                        new()
                                        {
                                            ["label"] = "Start",
                                            ["url"] = "/start",
                                            ["source_url"] = "https://example.com/start",
                                            ["scope"] = "internal",
                                            ["kind"] = "primary",
                                        },
                                    },
                                },
                            },
                            new ComponentNode
                            {
                                Id = "footer",
                                Type = "InstitutionFooter",
                                Props =
                                {
                                    ["source_url"] = "https://example.com/",
                                    ["logo_url"] = "",
                                    ["logo_alt"] = "",
                                    ["contact_text"] = "Address",
                                    ["links"] = new List<Dictionary<string, string>>(),
                                },
                            },
                        ],
                    },
                },
            ],
        };
        var generator = new StaticSitePackageGenerator();

        var result = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "template-components-site",
        });

        var runtime = File.ReadAllText(Path.Combine(result.OutputDirectory, "runtime.js"));
        runtime.Should().Contain("renderMegaHeader");
        runtime.Should().Contain("renderHeroCarousel");
        runtime.Should().Contain("renderQuickLinkRibbon");
        runtime.Should().Contain("renderNewsCardCarousel");
        runtime.Should().Contain("renderServiceSearchHero");
        runtime.Should().Contain("renderServiceCategoryGrid");
        runtime.Should().Contain("renderServiceActionGrid");
        runtime.Should().Contain("renderTabbedNewsBoard");
        runtime.Should().Contain("renderSearchBoxPanel");
        runtime.Should().Contain("renderFacetFilterPanel");
        runtime.Should().Contain("renderResultList");
        runtime.Should().Contain("renderPaginationNav");
        runtime.Should().Contain("renderDashboardFilterBar");
        runtime.Should().Contain("renderMetricSummaryGrid");
        runtime.Should().Contain("renderChartPanel");
        runtime.Should().Contain("renderDataTablePreview");
        runtime.Should().Contain("renderStepIndicator");
        runtime.Should().Contain("renderStructuredFormPanel");
        runtime.Should().Contain("renderValidationSummary");
        runtime.Should().Contain("renderFormActionBar");
        runtime.Should().Contain("renderShowcaseHero");
        runtime.Should().Contain("renderProductCardGrid");
        runtime.Should().Contain("renderProofStrip");
        runtime.Should().Contain("renderPricingPanel");
        runtime.Should().Contain("renderCtaBand");
        runtime.Should().Contain("renderInstitutionFooter");

        var styles = File.ReadAllText(Path.Combine(result.OutputDirectory, "styles.css"));
        styles.Should().Contain(".template-hero");
        styles.Should().Contain("align-items: center");
        styles.Should().Contain("height: clamp(260px, 36vw, 430px)");
        styles.Should().Contain(".quick-link-ribbon");
        styles.Should().Contain(".service-search-hero");
        styles.Should().Contain(".service-category-grid");
        styles.Should().Contain(".service-action-grid");
        styles.Should().Contain(".tabbed-news-board");
        styles.Should().Contain("background: #a71483");
        styles.Should().Contain(".tabbed-news-item { border: 0;");
        styles.Should().Contain(".media-feature-grid .template-card-body");
        styles.Should().Contain(".search-box-panel");
        styles.Should().Contain(".facet-filter-panel");
        styles.Should().Contain(".result-list");
        styles.Should().Contain(".dashboard-filter-bar");
        styles.Should().Contain(".metric-summary-grid");
        styles.Should().Contain(".chart-panel");
        styles.Should().Contain(".data-table-preview");
        styles.Should().Contain(".step-indicator");
        styles.Should().Contain(".structured-form-panel");
        styles.Should().Contain(".showcase-hero");
        styles.Should().Contain(".pricing-panel");
        styles.Should().Contain(".cta-band");
        styles.Should().Contain(".institution-footer");
    }

    [Fact]
    public void Generate_ThemeTokensSerializeInStableOrder_RegardlessOfInsertionOrder()
    {
        var ascending = BuildThemedDocument(
        [
            new("accent", "#00aa00"),
            new("brand", "#0000ff"),
            new("ink", "#111111"),
        ]);
        var descending = BuildThemedDocument(
        [
            new("ink", "#111111"),
            new("brand", "#0000ff"),
            new("accent", "#00aa00"),
        ]);
        var generator = new StaticSitePackageGenerator();

        var resultA = generator.Generate(ascending, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "theme-order-a",
        });
        var resultB = generator.Generate(descending, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "theme-order-b",
        });

        var siteA = File.ReadAllText(Path.Combine(resultA.OutputDirectory, "site.json"));
        var siteB = File.ReadAllText(Path.Combine(resultB.OutputDirectory, "site.json"));
        siteB.Should().Be(siteA);
        siteA.Should().Contain("\"accent\"").And.Contain("\"brand\"").And.Contain("\"ink\"");
    }

    [Fact]
    public void Generate_ProducesByteIdenticalArchive_AcrossRepeatedRuns()
    {
        var document = ComponentSchemaValidatorTests.BuildValidDocument();
        var generator = new StaticSitePackageGenerator();

        var first = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "determinism-a",
            EnforceQualityGate = true,
            CreateArchive = true,
        });
        var second = generator.Generate(document, new StaticSitePackageOptions
        {
            OutputDirectory = tempRoot,
            PackageName = "determinism-b",
            EnforceQualityGate = true,
            CreateArchive = true,
        });

        File.ReadAllBytes(second.ArchivePath).Should().Equal(File.ReadAllBytes(first.ArchivePath));

        using var archive = ZipFile.OpenRead(first.ArchivePath);
        archive.Entries.Select(entry => entry.FullName).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    private static GeneratorSiteDocument BuildThemedDocument(IReadOnlyList<KeyValuePair<string, string>> colors)
    {
        var document = new GeneratorSiteDocument
        {
            SchemaVersion = "site-generator/v1",
            Site = new GeneratorSiteMetadata { Title = "Example", SourceUrl = "https://example.com/" },
            ComponentLibrary = DefaultComponentLibrary.Create(),
            Routes =
            [
                new GeneratorRoute
                {
                    Path = "/",
                    Title = "Example",
                    Root = new ComponentNode
                    {
                        Id = "page",
                        Type = "PageShell",
                        Props =
                        {
                            ["title"] = "Example",
                            ["source_url"] = "https://example.com/",
                        },
                        Children =
                        [
                            new ComponentNode
                            {
                                Id = "hero",
                                Type = "ContentSection",
                                Props =
                                {
                                    ["title"] = "Example",
                                    ["body"] = "Welcome.",
                                },
                            },
                        ],
                    },
                },
            ],
        };
        foreach (var pair in colors)
        {
            document.Site.Theme.Colors[pair.Key] = pair.Value;
            document.Site.Theme.Typography["font_" + pair.Key] = pair.Value;
        }

        return document;
    }
}
