using System.IO;
using Broker.Services;
using BrokerCore.Data;
using BrokerCore.Models;

namespace Unit.Tests.Services;

/// <summary>
/// AzureIisDeploymentRequestBuilder.TryBuild —— 部署請求建構的純邏輯契約:
/// spec 守門 / 輸入完整性 / target 守門 / mode + path 正規化 / 專案檔解析 /
/// 預設值 / 欄位映射 / 輸出路徑組合。
///
/// 依賴策略(全確定性、可控):
///   - IToolSpecRegistry 用 NSubstitute(interface)假回傳 kind=deployment 的 spec。
///   - BrokerDb 用每測獨立的臨時檔 SQLite,EnsureTable + Insert 真實 target(Get&lt;T&gt; 非 virtual、不可 mock)。
///   - 檔案系統檢查用臨時目錄裡的真實 .csproj(可控 IO,非外部呼叫)。
/// 所有 I/O 在 Dispose 清掉(符合 CLAUDE.md 測試產物清理規則)。
/// </summary>
public sealed class AzureIisDeploymentRequestBuilderTests : IDisposable
{
    private const string ToolId = "azure_iis_deploy";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly BrokerDb _db;
    private readonly IToolSpecRegistry _registry;
    private readonly AzureIisDeploymentRequestBuilder _builder;

    // 臨時專案目錄裡放一個唯一 .csproj → ResolveProjectFile 走「目錄唯一 csproj」分支
    private readonly string _projectDir;
    private readonly string _csprojPath;

    public AzureIisDeploymentRequestBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "b4a-deploybuilder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _dbPath = Path.Combine(_root, "test.db");
        _db = BrokerDb.UseSqlite($"Data Source={_dbPath}");
        _db.EnsureTable<AzureIisDeploymentTarget>();

        _registry = Substitute.For<IToolSpecRegistry>();
        _registry.Get(ToolId).Returns(new ToolSpecView { ToolId = ToolId, Kind = "deployment" });

        _builder = new AzureIisDeploymentRequestBuilder(_registry, _db);

        _projectDir = Path.Combine(_root, "proj");
        Directory.CreateDirectory(_projectDir);
        _csprojPath = Path.Combine(_projectDir, "App.csproj");
        File.WriteAllText(_csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort 清理;殘留臨時檔不應讓測試失敗
        }
    }

    // ── helpers ───────────────────────────────────────────────

    private AzureIisDeploymentTarget InsertTarget(Action<AzureIisDeploymentTarget>? mutate = null)
    {
        var target = new AzureIisDeploymentTarget
        {
            TargetId = "tgt_" + Guid.NewGuid().ToString("N"),
            Provider = "azure_vm_iis",
            Transport = "winrm_powershell",
            Status = "active",
            DeploymentMode = "site_root",
            VmHost = "vm.example.com",
            Port = 5985,
            UseSsl = false,
            SiteName = "Default Web Site",
            AppPoolName = "DefaultAppPool",
            PhysicalPath = @"C:\inetpub\wwwroot",
            SecretRef = "secret_winrm",
        };
        mutate?.Invoke(target);
        _db.Insert(target);
        return target;
    }

    private AzureIisDeploymentBuildInput ValidInput(string targetId, Action<AzureIisDeploymentBuildInput>? mutate = null)
    {
        var input = new AzureIisDeploymentBuildInput
        {
            RequestId = "req_001",
            CapabilityId = "cap_deploy",
            Route = "/deploy",
            PrincipalId = "prn_user",
            TaskId = "task_001",
            SessionId = "sess_001",
            TargetId = targetId,
            ProjectPath = _projectDir,
        };
        mutate?.Invoke(input);
        return input;
    }

    // ── spec 守門 ─────────────────────────────────────────────

    [Fact]
    public void TryBuild_SpecNotFound_Fails()
    {
        _registry.Get("missing").Returns((ToolSpecView?)null);

        var r = _builder.TryBuild("missing", ValidInput(InsertTarget().TargetId));

        r.Success.Should().BeFalse();
        r.Error.Should().Be("tool_spec_not_found");
        r.Request.Should().BeNull();
    }

    [Fact]
    public void TryBuild_SpecNotDeploymentKind_Fails()
    {
        _registry.Get("browser_tool").Returns(new ToolSpecView { ToolId = "browser_tool", Kind = "browser" });

        var r = _builder.TryBuild("browser_tool", ValidInput(InsertTarget().TargetId));

        r.Error.Should().Be("tool_spec_not_deployment");
    }

    [Fact]
    public void TryBuild_DeploymentKindIsCaseInsensitive()
    {
        _registry.Get("upper").Returns(new ToolSpecView { ToolId = "upper", Kind = "DEPLOYMENT" });

        var r = _builder.TryBuild("upper", ValidInput(InsertTarget().TargetId));

        r.Success.Should().BeTrue();
    }

    // ── 輸入完整性(任一必填空白 → fail)───────────────────────

    [Theory]
    [InlineData("RequestId")]
    [InlineData("CapabilityId")]
    [InlineData("Route")]
    [InlineData("PrincipalId")]
    [InlineData("TaskId")]
    [InlineData("SessionId")]
    [InlineData("TargetId")]
    [InlineData("ProjectPath")]
    public void TryBuild_MissingRequiredInput_Fails(string fieldToBlank)
    {
        var targetId = InsertTarget().TargetId;
        var input = ValidInput(targetId, i =>
        {
            switch (fieldToBlank)
            {
                case "RequestId": i.RequestId = "   "; break;
                case "CapabilityId": i.CapabilityId = ""; break;
                case "Route": i.Route = ""; break;
                case "PrincipalId": i.PrincipalId = ""; break;
                case "TaskId": i.TaskId = ""; break;
                case "SessionId": i.SessionId = ""; break;
                case "TargetId": i.TargetId = ""; break;
                case "ProjectPath": i.ProjectPath = ""; break;
            }
        });

        var r = _builder.TryBuild(ToolId, input);

        r.Success.Should().BeFalse();
        r.Error.Should().Be("deployment_request_input_incomplete");
    }

    // ── target 守門 ───────────────────────────────────────────

    [Fact]
    public void TryBuild_TargetMissing_Fails()
    {
        var input = ValidInput("tgt_does_not_exist");

        var r = _builder.TryBuild(ToolId, input);

        r.Error.Should().Be("deployment_target_not_found");
    }

    [Fact]
    public void TryBuild_TargetInactive_Fails()
    {
        var target = InsertTarget(t => t.Status = "disabled");

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Error.Should().Be("deployment_target_not_found");
    }

    [Fact]
    public void TryBuild_TargetProviderMismatch_Fails()
    {
        var target = InsertTarget(t => t.Provider = "aws_ec2");

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Error.Should().Be("deployment_target_provider_mismatch");
    }

    [Fact]
    public void TryBuild_TransportNotSupported_Fails()
    {
        var target = InsertTarget(t => t.Transport = "ssh");

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Error.Should().Be("deployment_target_transport_not_supported");
    }

    [Fact]
    public void TryBuild_InvalidDeploymentMode_Fails()
    {
        var target = InsertTarget(t => t.DeploymentMode = "kubernetes");

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Error.Should().Be("deployment_target_mode_not_supported");
    }

    [Fact]
    public void TryBuild_IisApplicationModeWithoutApplicationPath_Fails()
    {
        var target = InsertTarget(t =>
        {
            t.DeploymentMode = "iis_application";
            t.ApplicationPath = "   ";
        });

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Error.Should().Be("deployment_application_path_required");
    }

    // ── 專案路徑守門 ──────────────────────────────────────────

    [Fact]
    public void TryBuild_RelativeProjectPath_Fails()
    {
        var target = InsertTarget();
        var input = ValidInput(target.TargetId, i => i.ProjectPath = "relative/path");

        var r = _builder.TryBuild(ToolId, input);

        r.Error.Should().Be("deployment_project_path_must_be_absolute");
    }

    [Fact]
    public void TryBuild_AbsoluteButNonexistentProjectPath_Fails()
    {
        var target = InsertTarget();
        var missing = Path.Combine(_root, "no-such-dir-" + Guid.NewGuid().ToString("N"));
        var input = ValidInput(target.TargetId, i => i.ProjectPath = missing);

        var r = _builder.TryBuild(ToolId, input);

        r.Error.Should().Be("deployment_project_path_not_found");
    }

    [Fact]
    public void TryBuild_DirectoryWithNoCsproj_Fails()
    {
        var target = InsertTarget();
        var emptyDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyDir);
        var input = ValidInput(target.TargetId, i => i.ProjectPath = emptyDir);

        var r = _builder.TryBuild(ToolId, input);

        r.Error.Should().Be("deployment_project_file_not_found");
    }

    [Fact]
    public void TryBuild_DirectoryWithMultipleCsproj_Fails()
    {
        var target = InsertTarget();
        var multiDir = Path.Combine(_root, "multi");
        Directory.CreateDirectory(multiDir);
        File.WriteAllText(Path.Combine(multiDir, "A.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(multiDir, "B.csproj"), "<Project />");
        var input = ValidInput(target.TargetId, i => i.ProjectPath = multiDir);

        var r = _builder.TryBuild(ToolId, input);

        // 多於一個 → 無法確定唯一專案檔
        r.Error.Should().Be("deployment_project_file_not_found");
    }

    [Fact]
    public void TryBuild_ProjectPathPointsDirectlyAtCsproj_ResolvesThatFile()
    {
        var target = InsertTarget();
        var input = ValidInput(target.TargetId, i => i.ProjectPath = _csprojPath);

        var r = _builder.TryBuild(ToolId, input);

        r.Success.Should().BeTrue();
        r.Request!.ProjectFile.Should().Be(Path.GetFullPath(_csprojPath));
    }

    // ── 成功 + 欄位映射 + 預設值 ─────────────────────────────

    [Fact]
    public void TryBuild_HappyPath_MapsTargetAndInputFields()
    {
        var target = InsertTarget(t =>
        {
            t.VmHost = "deploy.example.com";
            t.Port = 5986;
            t.UseSsl = true;
            t.SiteName = "MySite";
            t.AppPoolName = "MyPool";
            t.PhysicalPath = @"C:\sites\mysite";
            t.SecretRef = "vault_ref_x";
        });

        var input = ValidInput(target.TargetId);
        var r = _builder.TryBuild(ToolId, input);

        r.Success.Should().BeTrue();
        var req = r.Request!;

        // 來自 input
        req.RequestId.Should().Be("req_001");
        req.ToolId.Should().Be(ToolId);
        req.CapabilityId.Should().Be("cap_deploy");
        req.Route.Should().Be("/deploy");
        req.PrincipalId.Should().Be("prn_user");
        req.TaskId.Should().Be("task_001");
        req.SessionId.Should().Be("sess_001");

        // 來自 target
        req.TargetId.Should().Be(target.TargetId);
        req.Provider.Should().Be("azure_vm_iis");
        req.Transport.Should().Be("winrm_powershell");
        req.VmHost.Should().Be("deploy.example.com");
        req.Port.Should().Be(5986);
        req.UseSsl.Should().BeTrue();
        req.SiteName.Should().Be("MySite");
        req.AppPoolName.Should().Be("MyPool");
        req.PhysicalPath.Should().Be(@"C:\sites\mysite");
        req.SecretRef.Should().Be("vault_ref_x");

        // 解析結果
        req.ProjectPath.Should().Be(Path.GetFullPath(_projectDir));
        req.ProjectFile.Should().Be(_csprojPath);
    }

    [Fact]
    public void TryBuild_DefaultsApplied_WhenInputOptionalsBlank()
    {
        var target = InsertTarget();
        var input = ValidInput(target.TargetId, i =>
        {
            i.Configuration = "   ";
            i.RuntimeIdentifier = "   ";
            i.ScopeJson = "";
            i.MetadataJson = "   ";
        });

        var req = _builder.TryBuild(ToolId, input).Request!;

        req.Configuration.Should().Be("Release");
        req.RuntimeIdentifier.Should().BeNull();
        req.ScopeJson.Should().Be("{}");
        req.MetadataJson.Should().Be("{}");
    }

    [Fact]
    public void TryBuild_PassesThroughExplicitOptionals()
    {
        var target = InsertTarget();
        var input = ValidInput(target.TargetId, i =>
        {
            i.Configuration = "Debug";
            i.RuntimeIdentifier = "win-x64";
            i.SelfContained = true;
            i.CleanupTarget = false;
            i.RestartSite = false;
            i.ScopeJson = "{\"a\":1}";
            i.MetadataJson = "{\"b\":2}";
        });

        var req = _builder.TryBuild(ToolId, input).Request!;

        req.Configuration.Should().Be("Debug");
        req.RuntimeIdentifier.Should().Be("win-x64");
        req.SelfContained.Should().BeTrue();
        req.CleanupTarget.Should().BeFalse();
        req.RestartSite.Should().BeFalse();
        req.ScopeJson.Should().Be("{\"a\":1}");
        req.MetadataJson.Should().Be("{\"b\":2}");
    }

    [Fact]
    public void TryBuild_OutputPaths_DerivedFromRequestId()
    {
        var target = InsertTarget();
        var input = ValidInput(target.TargetId, i => i.RequestId = "req_xyz");

        var req = _builder.TryBuild(ToolId, input).Request!;

        var expectedRoot = Path.Combine(Path.GetTempPath(), "b4a-deploy", "req_xyz");
        req.PublishOutputPath.Should().Be(Path.Combine(expectedRoot, "publish"));
        req.PackagePath.Should().Be(Path.Combine(expectedRoot, "publish.zip"));
    }

    // ── deployment mode 正規化 ────────────────────────────────

    [Theory]
    [InlineData("site_root", "site_root")]
    [InlineData("SITE_ROOT", "site_root")]   // 大小寫不敏感
    [InlineData("  site_root  ", "site_root")] // 修剪空白
    public void TryBuild_NormalizesDeploymentMode_SiteRoot(string raw, string expected)
    {
        var target = InsertTarget(t => t.DeploymentMode = raw);

        var req = _builder.TryBuild(ToolId, ValidInput(target.TargetId)).Request!;

        req.DeploymentMode.Should().Be(expected);
    }

    [Fact]
    public void TryBuild_EmptyDeploymentMode_DefaultsToSiteRoot()
    {
        // NormalizeDeploymentMode: (mode ?? "site_root") —— 空字串會被視為無效…
        // 實際:string.Empty.Trim() == "" → switch 無 match → null → fail。
        // 用空白驗證「空白被視為未支援」(釘死 null-coalesce 只接 null、不接空字串)。
        var target = InsertTarget(t => t.DeploymentMode = "");

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Error.Should().Be("deployment_target_mode_not_supported");
    }

    // ── application path 正規化(iis_application 模式)─────────

    [Theory]
    [InlineData("api", "/api")]              // 補前導斜線
    [InlineData("/api/", "/api")]            // 去尾斜線
    [InlineData("//api//v1//", "/api/v1")]   // 收摺多重斜線 + 去尾
    [InlineData("/", "/")]                   // 根:長度=1 不去尾
    public void TryBuild_NormalizesApplicationPath(string raw, string expected)
    {
        var target = InsertTarget(t =>
        {
            t.DeploymentMode = "iis_application";
            t.ApplicationPath = raw;
        });

        var req = _builder.TryBuild(ToolId, ValidInput(target.TargetId)).Request!;

        req.ApplicationPath.Should().Be(expected);
    }

    [Fact]
    public void TryBuild_SiteRootMode_AllowsEmptyApplicationPath()
    {
        // site_root 模式不要求 application_path → 空白正規化成空字串、不 fail
        var target = InsertTarget(t =>
        {
            t.DeploymentMode = "site_root";
            t.ApplicationPath = "";
        });

        var r = _builder.TryBuild(ToolId, ValidInput(target.TargetId));

        r.Success.Should().BeTrue();
        r.Request!.ApplicationPath.Should().Be(string.Empty);
    }

    // ── health check path 正規化 ──────────────────────────────

    [Theory]
    [InlineData("", "")]                 // 空 → 空(不補斜線)
    [InlineData("healthz", "/healthz")]  // 補前導斜線
    [InlineData("/healthz", "/healthz")] // 已有斜線 → 原樣
    [InlineData("  /ping  ", "/ping")]   // 修剪後已有斜線
    public void TryBuild_NormalizesHealthCheckPath(string raw, string expected)
    {
        var target = InsertTarget(t => t.HealthCheckPath = raw);

        var req = _builder.TryBuild(ToolId, ValidInput(target.TargetId)).Request!;

        req.HealthCheckPath.Should().Be(expected);
    }

    [Fact]
    public void TryBuild_TrimsHealthCheckBaseUrl()
    {
        var target = InsertTarget(t => t.HealthCheckBaseUrl = "  https://app.example.com  ");

        var req = _builder.TryBuild(ToolId, ValidInput(target.TargetId)).Request!;

        req.HealthCheckBaseUrl.Should().Be("https://app.example.com");
    }
}
