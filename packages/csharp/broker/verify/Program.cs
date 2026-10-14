using System.Text.Json;
using Broker.Adapters;
using Broker.Services;
using BrokerCore.Contracts;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SiteCrawlerWorker.Services;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Text;

static void AssertTrue(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);

    Console.WriteLine($"  ✓ {message}");
}

static string ExtractCookieValue(HttpContext context, string cookieName)
{
    var setCookie = context.Response.Headers.SetCookie.ToString();
    var marker = cookieName + "=";
    var start = setCookie.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0)
        throw new Exception($"Cookie '{cookieName}' not found.");

    start += marker.Length;
    var end = setCookie.IndexOf(';', start);
    if (end < 0)
        end = setCookie.Length;
    return setCookie[start..end];
}

static DefaultHttpContext CreateLocalHttpContext(string? cookieValue = null)
{
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = IPAddress.Loopback;
    if (!string.IsNullOrWhiteSpace(cookieValue))
        context.Request.Headers["Cookie"] = $"{LocalAdminAuthService.SessionCookieName}={cookieValue}";
    return context;
}

var sandboxRoot = Path.Combine(Path.GetTempPath(), "broker-verify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandboxRoot);
var verifyProjectDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "packages", "csharp", "broker", "verify"));

try
{
    Console.WriteLine("=== Broker Verify ===");

    var parserOptions = new HighLevelCoordinatorOptions();
    var parser = new HighLevelCommandParser(parserOptions);
    var trustPolicy = new HighLevelInputTrustPolicy();
    var workflowMachine = new HighLevelWorkflowStateMachine();
    var promotionGate = new HighLevelExecutionPromotionGate();
    var browserRequest = new BrowserExecutionRequest
    {
        RequestId = "req_browser_1",
        ToolId = "browser.reference.anonymous.read",
        CapabilityId = "browser.read",
        Route = "browser_read",
        IdentityMode = "anonymous",
        CredentialBinding = "none",
        SessionBindingMode = "ephemeral",
        SessionReuseScope = "none",
        SiteBindingMode = "public_open",
        AllowedSiteClasses = new[] { "public_web" },
        MaxActionLevel = "navigate",
        RequiresHumanConfirmationOn = Array.Empty<string>(),
        PrincipalId = "principal_1",
        TaskId = "task_1",
        SessionId = "session_1",
        StartUrl = "https://example.com",
        IntendedActionLevel = "read"
    };
    var browserRequestJson = JsonSerializer.Serialize(browserRequest);
    var browserRequestRoundtrip = JsonSerializer.Deserialize<BrowserExecutionRequest>(browserRequestJson);
    AssertTrue(browserRequestRoundtrip != null && browserRequestRoundtrip.SiteBindingMode == "public_open", "browser execution request round-trips canonical site policy");
    var browserResult = BrowserExecutionResult.Ok("req_browser_1", "browser.reference.anonymous.read", "read", "https://example.com", title: "Example");
    var browserResultJson = JsonSerializer.Serialize(browserResult);
    var browserResultRoundtrip = JsonSerializer.Deserialize<BrowserExecutionResult>(browserResultJson);
    AssertTrue(browserResultRoundtrip != null && browserResultRoundtrip.ActionLevelReached == "read", "browser execution result round-trips canonical action level");
    AssertTrue(parser.Parse("?help").Kind == HighLevelInputKind.Help, "parser recognizes explicit help command");
    AssertTrue(parser.Parse("?h").Kind == HighLevelInputKind.Help, "parser recognizes help alias");
    AssertTrue(parser.Parse("/build website").Kind == HighLevelInputKind.Production, "parser recognizes production prefix");
    AssertTrue(parser.Parse("?weather taipei").Kind == HighLevelInputKind.Query, "parser recognizes query prefix");
    var searchParsed = parser.Parse("?search taipei weather");
    AssertTrue(searchParsed.QueryCommand == "search" && searchParsed.QueryArgument == "taipei weather", "parser extracts explicit search query subcommand");
    var searchAliasParsed = parser.Parse("?s taipei weather");
    AssertTrue(searchAliasParsed.QueryCommand == "search" && searchAliasParsed.QueryArgument == "taipei weather", "parser extracts search alias subcommand");
    var railParsed = parser.Parse("?rail 台北 台中 今天 18:00");
    AssertTrue(railParsed.QueryCommand == "rail" && railParsed.QueryArgument == "台北 台中 今天 18:00", "parser extracts explicit rail query subcommand");
    var railAliasParsed = parser.Parse("?r 台北 台中 今天 18:00");
    AssertTrue(railAliasParsed.QueryCommand == "rail" && railAliasParsed.QueryArgument == "台北 台中 今天 18:00", "parser extracts rail alias subcommand");
    var hsrParsed = parser.Parse("?hsr 台北 台中 今天 18:00");
    AssertTrue(hsrParsed.QueryCommand == "hsr" && hsrParsed.QueryArgument == "台北 台中 今天 18:00", "parser extracts explicit hsr query subcommand");
    var hsrAliasParsed = parser.Parse("?thsr 台北 台中 今天 18:00");
    AssertTrue(hsrAliasParsed.QueryCommand == "hsr" && hsrAliasParsed.QueryArgument == "台北 台中 今天 18:00", "parser extracts thsr alias subcommand");
    var busParsed = parser.Parse("?bus 台北 台中 今天 18:00");
    AssertTrue(busParsed.QueryCommand == "bus" && busParsed.QueryArgument == "台北 台中 今天 18:00", "parser extracts explicit bus query subcommand");
    var busAliasParsed = parser.Parse("?b 台北 台中 今天 18:00");
    AssertTrue(busAliasParsed.QueryCommand == "bus" && busAliasParsed.QueryArgument == "台北 台中 今天 18:00", "parser extracts bus alias subcommand");
    var flightParsed = parser.Parse("?flight TPE KIX tomorrow");
    AssertTrue(flightParsed.QueryCommand == "flight" && flightParsed.QueryArgument == "TPE KIX tomorrow", "parser extracts explicit flight query subcommand");
    var flightAliasParsed = parser.Parse("?f TPE KIX tomorrow");
    AssertTrue(flightAliasParsed.QueryCommand == "flight" && flightAliasParsed.QueryArgument == "TPE KIX tomorrow", "parser extracts flight alias subcommand");
    var profileParsed = parser.Parse("?profile");
    AssertTrue(profileParsed.QueryCommand == "profile", "parser extracts explicit profile query subcommand");
    var profileAliasParsed = parser.Parse("?p");
    AssertTrue(profileAliasParsed.QueryCommand == "profile", "parser extracts profile alias subcommand");
    var plainQueryParsed = parser.Parse("?weather taipei");
    AssertTrue(string.IsNullOrWhiteSpace(plainQueryParsed.QueryCommand), "parser keeps plain query text outside tool subcommands");
    var nameParsed = parser.Parse("/name 小布");
    AssertTrue(nameParsed.ProductionCommand == "name" && nameParsed.ProductionArgument == "小布", "parser extracts display-name production command");
    var nameAliasParsed = parser.Parse("/n 小布");
    AssertTrue(nameAliasParsed.ProductionCommand == "name" && nameAliasParsed.ProductionArgument == "小布", "parser extracts display-name alias command");
    var idParsed = parser.Parse("/id bricks001");
    AssertTrue(idParsed.ProductionCommand == "id" && idParsed.ProductionArgument == "bricks001", "parser extracts alphanumeric-id production command");
    var idAliasParsed = parser.Parse("/i bricks001");
    AssertTrue(idAliasParsed.ProductionCommand == "id" && idAliasParsed.ProductionArgument == "bricks001", "parser extracts alphanumeric-id alias command");
    AssertTrue(parser.Parse("#MySite").Kind == HighLevelInputKind.ProjectName, "parser recognizes project-name prefix");
    AssertTrue(parser.Parse("confirm").Kind == HighLevelInputKind.Confirm, "parser recognizes confirm token");
    AssertTrue(parser.Parse("y").Kind == HighLevelInputKind.Confirm, "parser recognizes short confirm token");
    AssertTrue(parser.Parse("cancel").Kind == HighLevelInputKind.Cancel, "parser recognizes cancel token");
    AssertTrue(parser.Parse("n").Kind == HighLevelInputKind.Cancel, "parser recognizes short cancel token");
    AssertTrue(parser.Parse("hello world").Kind == HighLevelInputKind.Conversation, "parser keeps bare text as conversation");
    var projectInterviewParsed = parser.ParseProjectInterviewCommand("/proj");
    AssertTrue(projectInterviewParsed.IsProjectInterview && projectInterviewParsed.Command == ProjectInterviewCommand.StartProjectInterview, "parser recognizes /proj project interview command");
    var projectInterviewApproveParsed = parser.ParseProjectInterviewCommand("/ok");
    AssertTrue(projectInterviewApproveParsed.IsProjectInterview && projectInterviewApproveParsed.Command == ProjectInterviewCommand.Approve, "parser recognizes /ok project interview command");

    var projectInterviewMachine = new ProjectInterviewStateMachine();
    var idleInterview = ProjectInterviewSessionState.CreateNew("line", "U123");
    AssertTrue(idleInterview.CurrentPhase == ProjectInterviewPhase.Idle, "project interview session starts idle");
    var startedInterview = projectInterviewMachine.ApplyCommand(idleInterview, ProjectInterviewCommand.StartProjectInterview);
    AssertTrue(startedInterview.CurrentPhase == ProjectInterviewPhase.CollectProjectName, "project interview start enters project-name collection");
    var namedInterview = startedInterview with
    {
        ProjectName = "Alpha Portal",
        ProjectFolderName = "alpha-portal",
        HasUniqueProjectFolder = true
    };
    var classifiedInterview = projectInterviewMachine.Advance(namedInterview, ProjectInterviewAdvanceReason.ProjectNameAccepted);
    AssertTrue(classifiedInterview.CurrentPhase == ProjectInterviewPhase.ClassifyProjectScale, "accepted project name advances to scale classification");
    var cancelledInterview = projectInterviewMachine.ApplyCommand(classifiedInterview, ProjectInterviewCommand.Cancel);
    AssertTrue(cancelledInterview.CurrentPhase == ProjectInterviewPhase.Cancelled, "project interview cancel reaches terminal cancelled state");
    var restatementService = new ProjectInterviewRestatementService();
    var restatementOptions = restatementService.BuildOptions(
        new[]
        {
            "This is a small internal admin tool with login.",
            "This is a customer-facing portal with member accounts."
        },
        "None of these is precise.");
    AssertTrue(restatementOptions.Count == 3, "restatement adds conservative escape option");
    AssertTrue(restatementOptions[^1].IsConservativeEscape, "restatement marks the conservative escape option");
    var emptyTaskDocument = ProjectInterviewTaskDocument.CreateEmpty("line", "U123");
    var promotedTaskDocument = emptyTaskDocument.PromoteConfirmedOption(restatementOptions[0], "user selected A");
    AssertTrue(promotedTaskDocument.Assertions.Count(a => a.Status == AssertionStatus.Confirmed) == 1, "explicit confirmation promotes assertions into confirmed state");
    var projectInterviewDbPath = Path.Combine(sandboxRoot, "project-interview-state.db");
    using (var stateDb = BrokerDb.UseSqlite($"Data Source={projectInterviewDbPath}"))
    {
        var stateDbInitializer = new BrokerDbInitializer(stateDb);
        stateDbInitializer.Initialize();
        var stateService = new ProjectInterviewStateService(stateDb);
        await stateService.SaveTaskDocumentAsync(promotedTaskDocument, CancellationToken.None);
        var reloadedTaskDocument = await stateService.LoadTaskDocumentAsync("line", "U123", CancellationToken.None);
        AssertTrue(reloadedTaskDocument.Assertions.Count == 1, "project interview state service round-trips persisted assertions");
        AssertTrue(reloadedTaskDocument.Assertions[0].Statement == "This is a small internal admin tool with login.", "project interview state service preserves assertion statement");
    }
    var templateCatalogRoot = Path.Combine(sandboxRoot, "project-interview-templates");
    Directory.CreateDirectory(Path.Combine(templateCatalogRoot, "member_portal"));
    Directory.CreateDirectory(Path.Combine(templateCatalogRoot, "dashboard"));
    File.WriteAllText(
        Path.Combine(templateCatalogRoot, "catalog.json"),
        """
        {
          "families": [
            {
              "template_id": "member_portal",
              "supported_project_scales": ["mini_app", "structured_app"],
              "manifest_path": "./member_portal/template.manifest.json"
            },
            {
              "template_id": "dashboard",
              "supported_project_scales": ["mini_app", "structured_app"],
              "manifest_path": "./dashboard/template.manifest.json"
            }
          ]
        }
        """);
    File.WriteAllText(
        Path.Combine(templateCatalogRoot, "member_portal", "template.manifest.json"),
        """
        {
          "template_id": "member_portal",
          "title": "Member Portal",
          "summary": "Protected portal with login and member areas.",
          "supported_project_scales": ["mini_app", "structured_app"],
          "required_sections": ["auth_gate"],
          "optional_modules": ["profile"],
          "supported_styles": ["clean_enterprise"],
          "supported_component_sets": ["core"]
        }
        """);
    File.WriteAllText(
        Path.Combine(templateCatalogRoot, "dashboard", "template.manifest.json"),
        """
        {
          "template_id": "dashboard",
          "title": "Dashboard",
          "summary": "Operational KPI and activity surface.",
          "supported_project_scales": ["mini_app", "structured_app"],
          "required_sections": ["dashboard_shell"],
          "optional_modules": ["alerts"],
          "supported_styles": ["clean_enterprise"],
          "supported_component_sets": ["core"]
        }
        """);
    var templateCatalogConfig = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectInterview:TemplateCatalogPath"] = Path.Combine(templateCatalogRoot, "catalog.json")
        })
        .Build();
    var templateCatalogService = new ProjectInterviewTemplateCatalogService(
        templateCatalogConfig,
        new FakeWebHostEnvironment(sandboxRoot));
    var miniAppFamilies = templateCatalogService.NarrowByScale("mini_app");
    AssertTrue(miniAppFamilies.Count == 2, "template catalog narrows families by project scale");
    AssertTrue(miniAppFamilies.Any(item => item.TemplateId == "member_portal"), "template catalog includes member_portal for mini_app");
    AssertTrue(miniAppFamilies.All(item => item.SupportedProjectScales.Contains("mini_app", StringComparer.OrdinalIgnoreCase)), "template catalog only returns families that support the requested scale");
    var compiler = new ProjectInterviewProjectDefinitionCompiler();
    var compileResult = compiler.Compile(
        version: 1,
        confirmedAssertions: new[]
        {
            "project_scale=mini_app",
            "template_family=member_portal",
            "enabled_module=auth",
            "enabled_module=dashboard",
            "style_profile=clean-enterprise"
        });
    AssertTrue(compileResult.Version == 1, "compiler stamps version");
    AssertTrue(compileResult.ProjectDefinition.TemplateFamily == "member_portal", "compiler projects selected template");
    AssertTrue(compileResult.Dag.Nodes.Any(node => node.NodeType == "project_instance_definition_json"), "dag contains canonical json node");
    var workflow = new ProjectInterviewWorkflowDesignService();
    var viewModel = workflow.BuildViewModel(
        taskId: "task-1",
        version: 2,
        projectDefinition: new ProjectInstanceDefinition("mini_app", "member_portal", new[] { "auth" }, "clean-enterprise"));
    AssertTrue(viewModel.Version == 2, "view model carries version");
    AssertTrue(viewModel.TemplateFamily == "member_portal", "view model carries template family");
    var pdfRenderer = new ProjectInterviewPdfRenderService();
    var pdf = pdfRenderer.Render(viewModel, "ABC123");
    AssertTrue(pdf.FileName == "workflow-design.v2.pdf", "pdf renderer stamps versioned filename");
    AssertTrue(pdf.MetadataDigest == "ABC123", "pdf renderer preserves metadata digest");
    var deliveryMessage = LineArtifactDeliveryService.BuildNotificationBody(
        "workflow-design.v2.pdf",
        @"D:\Bricks4Agent\packages\csharp\broker\review\workflow-design.v2.pdf",
        null);
    AssertTrue(!deliveryMessage.Contains(@"D:\", StringComparison.Ordinal), "user-facing review output does not expose internal windows paths");
    AssertTrue(!deliveryMessage.Contains("/packages/csharp/", StringComparison.Ordinal), "user-facing review output does not expose repo paths");
    AssertTrue(deliveryMessage.Contains("workflow-design.v2.pdf", StringComparison.Ordinal), "user-facing review output references versioned artifact");

    var trustedUserCommand = trustPolicy.Apply(
        new HighLevelInputEnvelope
        {
            RawText = "/build website",
            Source = HighLevelInputSource.UserMessage,
            Taint = HighLevelInputTaint.UserText
        },
        parser.Parse("/build website"));
    AssertTrue(trustedUserCommand.Parsed.Kind == HighLevelInputKind.Production, "trust policy allows raw user commands");

    var transformedCommand = trustPolicy.Apply(
        new HighLevelInputEnvelope
        {
            RawText = "/build website",
            Source = HighLevelInputSource.DecodedPayload,
            Taint = HighLevelInputTaint.TransformedText,
            Transforms = new List<HighLevelTransformKind> { HighLevelTransformKind.Base64Decode }
        },
        parser.Parse("/build website"));
    AssertTrue(transformedCommand.Parsed.Kind == HighLevelInputKind.Conversation, "trust policy downgrades transformed command-like content");
    AssertTrue(!transformedCommand.Trust.Allowed, "trust policy denies command extraction from transformed content");

    var externalInstruction = trustPolicy.Apply(
        new HighLevelInputEnvelope
        {
            RawText = "#InjectedProject",
            Source = HighLevelInputSource.RetrievedDocument,
            Taint = HighLevelInputTaint.ExternalText
        },
        parser.Parse("#InjectedProject"));
    AssertTrue(externalInstruction.Parsed.Kind == HighLevelInputKind.Conversation, "trust policy downgrades external instruction-like content");

    var awaitingProjectDraft = new HighLevelTaskDraft
    {
        RequiresProjectName = true,
        ProjectName = null
    };
    AssertTrue(
        workflowMachine.Evaluate(awaitingProjectDraft, parser.Parse("#MySite")).Action == HighLevelWorkflowAction.CaptureProjectName,
        "workflow accepts project-name command only in awaiting-project-name state");
    AssertTrue(
        workflowMachine.Evaluate(awaitingProjectDraft, parser.Parse("y")).Action == HighLevelWorkflowAction.RequestProjectNameFirst,
        "workflow blocks short confirmation before project name is captured");

    var pendingDraft = new HighLevelTaskDraft
    {
        RequiresProjectName = true,
        ProjectName = "MySite"
    };
    AssertTrue(
        workflowMachine.Evaluate(pendingDraft, parser.Parse("y")).Action == HighLevelWorkflowAction.ConfirmDraft,
        "workflow accepts short confirm after draft requirements are satisfied");
    AssertTrue(
        workflowMachine.Evaluate(null, parser.Parse("/build website")).Action == HighLevelWorkflowAction.StartProduction,
        "workflow starts production only from explicit production command");

    var promotableMemory = new HighLevelMemoryState
    {
        Channel = "line",
        UserId = "tester",
        CurrentGoal = "build website",
        CurrentGoalCommitLevel = HighLevelMemoryCommitLevel.Candidate.ToString(),
        LastRouteMode = HighLevelRouteMode.Production.ToString(),
        ProjectName = "MySite",
        ProjectNameCommitLevel = HighLevelMemoryCommitLevel.Confirmed.ToString()
    };
    var promotableDraft = new HighLevelTaskDraft
    {
        DraftId = "draft_123",
        Channel = "line",
        UserId = "tester",
        TaskType = "code_gen",
        OriginalMessage = "/build website",
        ScopeDescriptor = "{}",
        RuntimeDescriptor = "{}",
        RequiresProjectName = true,
        ProjectName = "MySite"
    };
    var promotion = promotionGate.Evaluate(promotableMemory, promotableDraft);
    AssertTrue(promotion.Allowed, "promotion gate allows confirmed project metadata plus candidate user goal");

    var deniedPromotion = promotionGate.Evaluate(promotableMemory, new HighLevelTaskDraft
    {
        DraftId = "draft_456",
        Channel = "line",
        UserId = "tester",
        TaskType = "code_gen",
        OriginalMessage = "/build website",
        ScopeDescriptor = "{}",
        RuntimeDescriptor = "{}",
        RequiresProjectName = true,
        ProjectName = null
    });
    AssertTrue(!deniedPromotion.Allowed, "promotion gate denies execution without required project name");

    var mediator = new HighLevelQueryToolMediator(
        new FakeToolSpecRegistry(),
        new FakeExecutionDispatcher(),
        NullLogger<HighLevelQueryToolMediator>.Instance);
    var mediatedSearch = await mediator.SearchWebAsync("line", "tester", "taipei weather");
    AssertTrue(mediatedSearch.Success, "query tool mediator executes explicit search through broker-owned tool binding");
    AssertTrue(mediatedSearch.Reply.Contains("google", StringComparison.OrdinalIgnoreCase), "query tool mediator reply cites Google as the primary search engine");
    AssertTrue(mediatedSearch.Reply.Contains("https://example.com/weather", StringComparison.OrdinalIgnoreCase), "query tool mediator reply includes ranked URLs");
    AssertTrue(HighLevelRelationQueryService.TryExtractAdministrativeRelationQuery("北京市附近的行政區", out var relationPlan), "relation query service recognizes administrative relation prompt");
    AssertTrue(relationPlan.Subject == "北京市", "relation query service extracts core subject");
    var relationQueryService = new HighLevelRelationQueryService(
        mediator,
        new HighLevelLlmOptions
        {
            Provider = "ollama",
            BaseUrl = "http://localhost:11434",
            DefaultModel = "verify"
        },
        new FakeHttpClientFactory(),
        NullLogger<HighLevelRelationQueryService>.Instance);
    var relationAnswer = await relationQueryService.TryAnswerAsync("line", "tester", "北京市附近的行政區");
    AssertTrue(relationAnswer.Handled, "relation query service handles administrative relation queries");
    AssertTrue(!string.IsNullOrWhiteSpace(relationAnswer.Reply), "relation query service returns a non-empty evidence-based answer");
    var mediatedRail = await mediator.SearchRailAsync("line", "tester", "台北 台中 今天 18:00");
    AssertTrue(mediatedRail.Success, "query tool mediator executes explicit rail query through broker-owned transport tool");
    AssertTrue(mediatedRail.Reply.Contains("18:30", StringComparison.OrdinalIgnoreCase), "rail query mediator reply includes candidate time information");
    AssertTrue(mediatedRail.Reply.Contains("TDX", StringComparison.OrdinalIgnoreCase), "rail query mediator reply cites TDX as the source");
    var mediatedHsr = await mediator.SearchHsrAsync("line", "tester", "台北 台中 今天 18:00");
    AssertTrue(mediatedHsr.Success, "query tool mediator executes explicit hsr query through broker-owned transport tool");
    AssertTrue(mediatedHsr.Reply.Contains("06:15", StringComparison.OrdinalIgnoreCase), "hsr query mediator reply includes candidate time information");
    AssertTrue(mediatedHsr.Reply.Contains("TDX", StringComparison.OrdinalIgnoreCase), "hsr query mediator reply cites TDX as the source");
    var mediatedBus = await mediator.SearchBusAsync("line", "tester", "臺北市 307");
    AssertTrue(mediatedBus.Success, "query tool mediator executes explicit bus query through broker-owned transport tool");
    AssertTrue(mediatedBus.Reply.Contains("TDX", StringComparison.OrdinalIgnoreCase), "bus query mediator reply cites TDX as the source");
    AssertTrue(mediatedBus.Reply.Contains("4", StringComparison.OrdinalIgnoreCase), "bus query mediator reply includes ETA information");
    var mediatedFlight = await mediator.SearchFlightAsync("line", "tester", "松山 金門");
    AssertTrue(mediatedFlight.Success, "query tool mediator executes explicit flight query through broker-owned transport tool");
    AssertTrue(mediatedFlight.Reply.Contains("TDX", StringComparison.OrdinalIgnoreCase), "flight query mediator reply cites TDX as the source");

    var specRoot = Path.Combine(sandboxRoot, "tool-specs");
    Directory.CreateDirectory(Path.Combine(specRoot, "browser.reference.anonymous.read"));
    File.WriteAllText(
        Path.Combine(specRoot, "browser.reference.anonymous.read", "tool.json"),
        """
        {
          "tool_id": "browser.reference.anonymous.read",
          "display_name": "Browser Reference",
          "summary": "reference",
          "kind": "browser",
          "status": "active",
          "version": "2026-03-22",
          "tags": ["browser"],
          "capability_bindings": [
            {
              "capability_id": "browser.read",
              "route": "browser_read",
              "purpose": "Broker-governed anonymous public browser read"
            }
          ],
          "browser_profile": {
            "identity_mode": "anonymous",
            "credential_source": "none",
            "session_owner": "none",
            "allowed_actions": ["read", "navigate"],
            "confirmation_policy": "broker_policy"
          },
          "browser_session_policy": {
            "binding_mode": "ephemeral",
            "credential_binding": "none",
            "reuse_scope": "none",
            "lease_minutes": 15,
            "requires_consent_record": false,
            "requires_interactive_login": false
          },
          "browser_site_policy": {
            "site_binding_mode": "public_open",
            "allowed_site_classes": ["public_web"],
            "requires_registered_site_binding": false,
            "requires_exact_origin_match": false,
            "allows_cross_origin_navigation": true
          },
          "browser_action_policy": {
            "max_action_level": "navigate",
            "requires_human_confirmation_on": [],
            "allows_form_fill": false,
            "allows_submit": false,
            "allows_download": false,
            "allows_file_upload": false
          },
          "input_schema": { "type": "object" },
          "output_schema": { "type": "object" },
          "source_policy": { "allowed_sources": ["public_web"] },
          "execution_rules": { "runtime_required": "browser_worker" },
          "response_contract": { "must_identify_identity_mode": true }
        }
        """);
    File.WriteAllText(
        Path.Combine(specRoot, "browser.reference.anonymous.read", "TOOL.md"),
        "# Reference\n\nStatus: active");
    Directory.CreateDirectory(Path.Combine(specRoot, "browser.reference.system-account.read"));
    File.WriteAllText(
        Path.Combine(specRoot, "browser.reference.system-account.read", "tool.json"),
        """
        {
          "tool_id": "browser.reference.system-account.read",
          "display_name": "Browser System Reference",
          "summary": "system",
          "kind": "browser",
          "status": "planned",
          "version": "2026-03-22",
          "tags": ["browser"],
          "capability_bindings": [],
          "browser_profile": {
            "identity_mode": "system_account",
            "credential_source": "system_vault",
            "session_owner": "system",
            "allowed_actions": ["read", "navigate", "authenticate"],
            "confirmation_policy": "broker_policy"
          },
          "browser_session_policy": {
            "binding_mode": "broker_managed",
            "credential_binding": "system_vault",
            "reuse_scope": "site",
            "lease_minutes": 240,
            "requires_consent_record": false,
            "requires_interactive_login": false
          },
          "browser_site_policy": {
            "site_binding_mode": "registered_site",
            "allowed_site_classes": ["broker_managed_site"],
            "requires_registered_site_binding": true,
            "requires_exact_origin_match": true,
            "allows_cross_origin_navigation": false
          },
          "browser_action_policy": {
            "max_action_level": "authenticate",
            "requires_human_confirmation_on": [],
            "allows_form_fill": false,
            "allows_submit": false,
            "allows_download": false,
            "allows_file_upload": false
          },
          "input_schema": { "type": "object" },
          "output_schema": { "type": "object" },
          "source_policy": { "allowed_sources": ["broker_managed_site_bindings"] },
          "execution_rules": { "runtime_required": "browser_worker" },
          "response_contract": { "must_identify_identity_mode": true }
        }
        """);
    Directory.CreateDirectory(Path.Combine(specRoot, "browser.reference.user-delegated.read"));
    File.WriteAllText(
        Path.Combine(specRoot, "browser.reference.user-delegated.read", "tool.json"),
        """
        {
          "tool_id": "browser.reference.user-delegated.read",
          "display_name": "Browser User Reference",
          "summary": "user",
          "kind": "browser",
          "status": "planned",
          "version": "2026-03-22",
          "tags": ["browser"],
          "capability_bindings": [],
          "browser_profile": {
            "identity_mode": "user_delegated",
            "credential_source": "user_grant",
            "session_owner": "user",
            "allowed_actions": ["read", "navigate", "authenticate"],
            "confirmation_policy": "user_required"
          },
          "browser_session_policy": {
            "binding_mode": "user_bound",
            "credential_binding": "user_grant",
            "reuse_scope": "user",
            "lease_minutes": 120,
            "requires_consent_record": true,
            "requires_interactive_login": true
          },
          "browser_site_policy": {
            "site_binding_mode": "user_authorized_site",
            "allowed_site_classes": ["user_authorized_site"],
            "requires_registered_site_binding": true,
            "requires_exact_origin_match": true,
            "allows_cross_origin_navigation": false
          },
          "browser_action_policy": {
            "max_action_level": "authenticate",
            "requires_human_confirmation_on": ["authenticate"],
            "allows_form_fill": false,
            "allows_submit": false,
            "allows_download": false,
            "allows_file_upload": false
          },
          "input_schema": { "type": "object" },
          "output_schema": { "type": "object" },
          "source_policy": { "allowed_sources": ["user_authorized_site_bindings"] },
          "execution_rules": { "runtime_required": "browser_worker" },
          "response_contract": { "must_identify_identity_mode": true }
        }
        """);
    Directory.CreateDirectory(Path.Combine(specRoot, "browser.invalid.missing-action"));
    File.WriteAllText(
        Path.Combine(specRoot, "browser.invalid.missing-action", "tool.json"),
        """
        {
          "tool_id": "browser.invalid.missing-action",
          "display_name": "Invalid Browser Reference",
          "summary": "invalid",
          "kind": "browser",
          "status": "planned",
          "version": "2026-03-22",
          "tags": ["browser"],
          "capability_bindings": [],
          "browser_profile": {
            "identity_mode": "anonymous",
            "credential_source": "none",
            "session_owner": "none",
            "allowed_actions": ["read"],
            "confirmation_policy": "broker_policy"
          },
          "browser_session_policy": {
            "binding_mode": "ephemeral",
            "credential_binding": "none",
            "reuse_scope": "none",
            "lease_minutes": 15,
            "requires_consent_record": false,
            "requires_interactive_login": false
          },
          "browser_site_policy": {
            "site_binding_mode": "public_open",
            "allowed_site_classes": ["public_web"],
            "requires_registered_site_binding": false,
            "requires_exact_origin_match": false,
            "allows_cross_origin_navigation": true
          },
          "input_schema": { "type": "object" },
          "output_schema": { "type": "object" },
          "source_policy": { "allowed_sources": ["public_web"] },
          "execution_rules": { "runtime_required": "browser_worker" },
          "response_contract": { "must_identify_identity_mode": true }
        }
        """);
    Directory.CreateDirectory(Path.Combine(specRoot, "deploy.azure-vm-iis"));
    File.WriteAllText(
        Path.Combine(specRoot, "deploy.azure-vm-iis", "tool.json"),
        """
        {
          "tool_id": "deploy.azure-vm-iis",
          "display_name": "Deploy to Azure VM IIS",
          "summary": "deployment",
          "kind": "deployment",
          "status": "beta",
          "version": "2026-03-22",
          "tags": ["deployment", "azure", "iis"],
          "capability_template": {
            "action_type": "write",
            "resource_type": "deployment",
            "risk_level": "high",
            "approval_policy": "require_approval",
            "ttl_seconds": 1800,
            "audit_level": "full",
            "quota": { "max_calls": 10, "per_window_seconds": 3600 }
          },
          "capability_bindings": [
            {
              "capability_id": "deploy.azure-vm-iis",
              "route": "deploy_azure_vm_iis",
              "purpose": "test deployment"
            }
          ],
          "input_schema": {
            "type": "object",
            "properties": {
              "target_id": { "type": "string" },
              "project_path": { "type": "string" }
            },
            "required": ["target_id", "project_path"]
          },
          "output_schema": { "type": "object" },
          "source_policy": { "allowed_sources": ["broker_registered_deployment_targets"] },
          "execution_rules": { "transport": "winrm_powershell" },
          "response_contract": { "must_identify_target": true }
        }
        """);
    File.WriteAllText(
        Path.Combine(specRoot, "deploy.azure-vm-iis", "TOOL.md"),
        "# Deploy");

    var toolSpecDbPath = Path.Combine(sandboxRoot, "tool-spec-registry.db");
    using (var toolSpecDb = BrokerDb.UseSqlite($"Data Source={toolSpecDbPath}"))
    {
        var initializer = new BrokerDbInitializer(toolSpecDb);
        initializer.Initialize();
        toolSpecDb.Insert(new BrowserSiteBinding
        {
            SiteBindingId = "site_binding_public",
            DisplayName = "Public Example",
            IdentityMode = "anonymous",
            SiteClass = "public_web",
            Origin = "https://example.com",
            Status = "active"
        });
        toolSpecDb.Insert(new BrowserSiteBinding
        {
            SiteBindingId = "site_binding_system",
            DisplayName = "System Example",
            IdentityMode = "system_account",
            SiteClass = "broker_managed_site",
            Origin = "https://system.example.com",
            Status = "active"
        });
        toolSpecDb.Insert(new BrowserSiteBinding
        {
            SiteBindingId = "site_binding_user",
            DisplayName = "User Example",
            IdentityMode = "user_delegated",
            SiteClass = "user_authorized_site",
            Origin = "https://user.example.com",
            PrincipalId = "principal_user",
            Status = "active"
        });
        toolSpecDb.Insert(new BrowserUserGrant
        {
            UserGrantId = "grant_user_1",
            PrincipalId = "principal_user",
            SiteBindingId = "site_binding_user",
            Status = "active",
            ConsentRef = "consent_1"
        });
        toolSpecDb.Insert(new BrowserSystemBinding
        {
            SystemBindingId = "system_binding_1",
            DisplayName = "System Binding",
            SiteBindingId = "site_binding_system",
            Status = "active",
            SecretRef = "vault://system/example"
        });
        toolSpecDb.Insert(new BrowserSessionLease
        {
            SessionLeaseId = "lease_user_1",
            ToolId = "browser.reference.user-delegated.read",
            SiteBindingId = "site_binding_user",
            PrincipalId = "principal_user",
            IdentityMode = "user_delegated",
            LeaseState = "active",
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        });
        var registry = new ToolSpecRegistry(
            new FakeWebHostEnvironment(Path.Combine(sandboxRoot, "content-root")),
            new ToolSpecRegistryOptions { Root = specRoot },
            toolSpecDb,
            NullLogger<ToolSpecRegistry>.Instance);
        var browserSpec = registry.Get("browser.reference.anonymous.read");
        AssertTrue(browserSpec != null, "tool spec registry loads browser reference spec");
        AssertTrue(browserSpec!.BrowserProfile != null, "tool spec registry preserves browser profile");
        AssertTrue(browserSpec.BrowserProfile!.IdentityMode == "anonymous", "browser profile keeps identity mode");
        AssertTrue(browserSpec.BrowserProfile.CredentialSource == "none", "browser profile keeps credential source");
        AssertTrue(browserSpec.BrowserSessionPolicy != null, "tool spec registry preserves browser session policy");
        AssertTrue(browserSpec.BrowserSessionPolicy!.BindingMode == "ephemeral", "browser session policy keeps binding mode");
        AssertTrue(browserSpec.BrowserSessionPolicy.CredentialBinding == "none", "browser session policy keeps credential binding");
        AssertTrue(browserSpec.BrowserSitePolicy != null, "tool spec registry preserves browser site policy");
        AssertTrue(browserSpec.BrowserSitePolicy!.SiteBindingMode == "public_open", "browser site policy keeps site binding mode");
        AssertTrue(browserSpec.BrowserSitePolicy.AllowedSiteClasses.Length == 1 && browserSpec.BrowserSitePolicy.AllowedSiteClasses[0] == "public_web", "browser site policy keeps allowed site classes");
        AssertTrue(browserSpec.BrowserActionPolicy != null, "tool spec registry preserves browser action policy");
        AssertTrue(browserSpec.BrowserActionPolicy!.MaxActionLevel == "navigate", "browser action policy keeps max action level");
        AssertTrue(browserSpec.BrowserActionPolicy.RequiresHumanConfirmationOn.Length == 0, "browser action policy keeps confirmation requirements");
        AssertTrue(registry.Get("browser.invalid.missing-action") == null, "tool spec registry rejects incomplete browser specs");

        var builder = new BrowserExecutionRequestBuilder(registry, toolSpecDb);
        var builtAnonymous = builder.TryBuild("browser.reference.anonymous.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_1",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_public"
        });
        AssertTrue(builtAnonymous.Success, "browser request builder builds anonymous browser request from registry metadata");
        AssertTrue(builtAnonymous.Request!.IdentityMode == "anonymous", "browser request builder projects identity mode into runtime contract");
        var builtAnonymousWithoutBinding = builder.TryBuild("browser.reference.anonymous.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_1b",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read"
        });
        AssertTrue(builtAnonymousWithoutBinding.Success, "browser request builder allows anonymous public-open reads without registered site binding");

        var builtTooPowerful = builder.TryBuild("browser.reference.anonymous.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_2",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "committed_action"
        });
        AssertTrue(!builtTooPowerful.Success && builtTooPowerful.Error == "browser_request_action_level_exceeds_policy", "browser request builder denies action level above browser policy");

        var builtSystemMissing = builder.TryBuild("browser.reference.system-account.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_3",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_system"
        });
        AssertTrue(!builtSystemMissing.Success && builtSystemMissing.Error == "browser_request_missing_system_binding", "browser request builder requires system binding for system-account tools");

        var builtSystemSuccess = builder.TryBuild("browser.reference.system-account.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_3b",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_system",
            SystemBindingId = "system_binding_1"
        });
        AssertTrue(builtSystemSuccess.Success, "browser request builder accepts matching system binding record");

        var builtUserMissingGrant = builder.TryBuild("browser.reference.user-delegated.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_4",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_user",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_user"
        });
        AssertTrue(!builtUserMissingGrant.Success && builtUserMissingGrant.Error == "browser_request_missing_user_grant", "browser request builder requires user grant for user-delegated tools");

        var builtUserPrincipalMismatch = builder.TryBuild("browser.reference.user-delegated.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_5",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_other",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_user",
            UserGrantId = "grant_1"
        });
        AssertTrue(!builtUserPrincipalMismatch.Success && builtUserPrincipalMismatch.Error == "browser_request_site_binding_principal_mismatch", "browser request builder enforces user-delegated site binding ownership");

        var builtUserSuccess = builder.TryBuild("browser.reference.user-delegated.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_6",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_user",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_user",
            UserGrantId = "grant_user_1"
        });
        AssertTrue(builtUserSuccess.Success, "browser request builder accepts matching user grant record");
        AssertTrue(builtUserSuccess.Request!.SiteBindingId == "site_binding_user", "browser request builder preserves selected site binding in runtime request");

        var builtUserLeaseMismatch = builder.TryBuild("browser.reference.user-delegated.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_7",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_other",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_user",
            UserGrantId = "grant_user_1",
            SessionLeaseId = "lease_user_1"
        });
        AssertTrue(!builtUserLeaseMismatch.Success && builtUserLeaseMismatch.Error == "browser_request_site_binding_principal_mismatch", "browser request builder still denies mismatched site owner before lease acceptance");

        var builtUserWithLease = builder.TryBuild("browser.reference.user-delegated.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_built_8",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_user",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_user",
            UserGrantId = "grant_user_1",
            SessionLeaseId = "lease_user_1"
        });
        AssertTrue(builtUserWithLease.Success, "browser request builder accepts matching active session lease");

        var bindingService = new BrowserBindingService(toolSpecDb);
        var createdSite = bindingService.UpsertSiteBinding(new BrowserSiteBinding
        {
            DisplayName = "Created Site",
            IdentityMode = "anonymous",
            SiteClass = "public_web",
            Origin = "https://created.example.com",
            Status = "active"
        });
        AssertTrue(!string.IsNullOrWhiteSpace(createdSite.SiteBindingId), "browser binding service creates site bindings");

        var createdGrant = bindingService.UpsertUserGrant(new BrowserUserGrant
        {
            PrincipalId = "principal_created",
            ConsentRef = "consent_created",
            SiteBindingId = createdSite.SiteBindingId,
            Status = "active"
        });
        AssertTrue(!string.IsNullOrWhiteSpace(createdGrant.UserGrantId), "browser binding service creates user grants");

        var createdSystemBinding = bindingService.UpsertSystemBinding(new BrowserSystemBinding
        {
            DisplayName = "Created System",
            SiteBindingId = "site_binding_system",
            Status = "active",
            SecretRef = "vault://created/system"
        });
        AssertTrue(!string.IsNullOrWhiteSpace(createdSystemBinding.SystemBindingId), "browser binding service creates system bindings");

        var issuedLease = bindingService.IssueSessionLease(
            toolId: "browser.reference.anonymous.read",
            principalId: "principal_created",
            identityMode: "anonymous",
            expiresAt: DateTime.UtcNow.AddMinutes(15),
            siteBindingId: createdSite.SiteBindingId);
        AssertTrue(!string.IsNullOrWhiteSpace(issuedLease.SessionLeaseId), "browser binding service issues session leases");

        var revokedLease = bindingService.RevokeSessionLease(issuedLease.SessionLeaseId);
        AssertTrue(revokedLease != null && revokedLease.LeaseState == "revoked", "browser binding service revokes session leases");

        var previewHttpClient = new HttpClient(new FakeBrowserPreviewHandler())
        {
            BaseAddress = new Uri("https://preview.test/")
        };
        var previewService = new BrowserExecutionPreviewService(builder, previewHttpClient);
        var previewResult = await previewService.ExecuteAnonymousReadAsync("browser.reference.anonymous.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_preview_1",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            SiteBindingId = "site_binding_public"
        });
        AssertTrue(previewResult.Success, "browser preview service fetches anonymous public content");
        AssertTrue(previewResult.Result != null && previewResult.Result.Title == "Example Preview", "browser preview service extracts page title");

        var auditService = new AuditService(toolSpecDb);
        var sharedContextService = new SharedContextService(toolSpecDb, auditService);
        var activeLease = bindingService.IssueSessionLease(
            toolId: "browser.reference.anonymous.read",
            principalId: "principal_1",
            identityMode: "anonymous",
            expiresAt: DateTime.UtcNow.AddMinutes(15),
            siteBindingId: "site_binding_public");
        var runtimeHttpClient = new HttpClient(new FakeBrowserPreviewHandler())
        {
            BaseAddress = new Uri("https://runtime.test/")
        };
        var runtimeService = new BrowserExecutionRuntimeService(builder, runtimeHttpClient, toolSpecDb, sharedContextService, bindingService);
        var runtimeResult = await runtimeService.ExecuteAnonymousReadAsync("browser.reference.anonymous.read", new BrowserExecutionRequestBuildInput
        {
            RequestId = "req_browser_runtime_1",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            StartUrl = "https://example.com",
            IntendedActionLevel = "navigate",
            SiteBindingId = "site_binding_public",
            SessionLeaseId = activeLease.SessionLeaseId
        });
        AssertTrue(runtimeResult.Success, "browser runtime service executes anonymous public read");
        AssertTrue(runtimeResult.Result != null && runtimeResult.Result.EvidenceRef == "browser.execution.req_browser_runtime_1", "browser runtime service emits evidence reference");
        var runtimeEvidence = sharedContextService.ReadLatest("browser.execution.req_browser_runtime_1", "principal_1");
        AssertTrue(runtimeEvidence != null && runtimeEvidence.ContentType == "application/evidence", "browser runtime service persists browser evidence");
        var runtimeExecutions = runtimeService.ListRecentExecutions();
        AssertTrue(runtimeExecutions.Count > 0 && runtimeExecutions[0].DocumentId == "browser.execution.req_browser_runtime_1", "browser runtime service lists recent browser evidence");
        var touchedLease = bindingService.GetSessionLease(activeLease.SessionLeaseId);
        AssertTrue(touchedLease?.LastUsedAt != null, "browser runtime service touches active lease when used");
        var browserDispatcher = new InProcessDispatcher(
            NullLogger<InProcessDispatcher>.Instance,
            sandboxRoot,
            browserExecutionRuntimeService: runtimeService,
            db: toolSpecDb);
        var browserDispatch = await browserDispatcher.DispatchAsync(new ApprovedRequest
        {
            RequestId = "req_browser_dispatch_1",
            CapabilityId = "browser.read",
            Route = "browser_read",
            PrincipalId = "principal_1",
            TaskId = "task_1",
            SessionId = "session_1",
            Payload = """
                      {
                        "route": "browser_read",
                        "args": {
                          "url": "https://example.com",
                          "tool_id": "browser.reference.anonymous.read",
                          "action_level": "read"
                        },
                        "scope": {}
                      }
                      """
        });
        AssertTrue(browserDispatch.Success, "in-process dispatcher executes browser.read route");
        AssertTrue(browserDispatch.ResultPayload != null && browserDispatch.ResultPayload.Contains("browser.execution.req_browser_dispatch_1", StringComparison.Ordinal), "in-process dispatcher returns browser runtime evidence payload");

        var deploymentTargetService = new AzureIisDeploymentTargetService(toolSpecDb);
        var deploymentTarget = deploymentTargetService.UpsertTarget(new AzureIisDeploymentTarget
        {
            DisplayName = "Azure IIS Test",
            Provider = "azure_vm_iis",
            VmHost = "vm.example.com",
            Port = 5986,
            UseSsl = true,
            Transport = "winrm_powershell",
            SiteName = "Default Web Site",
            AppPoolName = "DefaultAppPool",
            PhysicalPath = @"C:\inetpub\wwwroot\TestApp",
            HealthCheckPath = "/health",
            HealthCheckBaseUrl = "https://deploy.example.com",
            SecretRef = "vault://deploy/test",
            Status = "active"
        });
        AssertTrue(!string.IsNullOrWhiteSpace(deploymentTarget.TargetId), "deployment target service upserts azure iis target");

        var deploymentSubAppTarget = deploymentTargetService.UpsertTarget(new AzureIisDeploymentTarget
        {
            DisplayName = "Azure IIS Child App",
            Provider = "azure_vm_iis",
            VmHost = "vm.example.com",
            Port = 5986,
            UseSsl = true,
            Transport = "winrm_powershell",
            SiteName = "Default Web Site",
            DeploymentMode = "iis_application",
            ApplicationPath = "/apps/verify",
            AppPoolName = "VerifyChildPool",
            PhysicalPath = @"C:\inetpub\apps\verify",
            HealthCheckPath = "/health",
            HealthCheckBaseUrl = "https://subapp.example.com",
            SecretRef = "vault://deploy/test",
            Status = "active"
        });
        AssertTrue(!string.IsNullOrWhiteSpace(deploymentSubAppTarget.TargetId), "deployment target service upserts child-application target");
        try
        {
            deploymentTargetService.UpsertTarget(new AzureIisDeploymentTarget
            {
                DisplayName = "Azure IIS Child App Duplicate",
                Provider = "azure_vm_iis",
                VmHost = "vm.example.com",
                Port = 5986,
                UseSsl = true,
                Transport = "winrm_powershell",
                SiteName = "Default Web Site",
                DeploymentMode = "iis_application",
                ApplicationPath = "/apps/verify",
                AppPoolName = "VerifyChildPool2",
                PhysicalPath = @"C:\inetpub\apps\verify-dup",
                SecretRef = "vault://deploy/test",
                Status = "active"
            });
            throw new Exception("Expected duplicate child-application target registration to fail.");
        }
        catch (InvalidOperationException)
        {
            AssertTrue(true, "deployment target service rejects duplicate child-application route on the same site");
        }

        var deploymentBuilder = new AzureIisDeploymentRequestBuilder(registry, toolSpecDb);
        var deploymentBuild = deploymentBuilder.TryBuild("deploy.azure-vm-iis", new AzureIisDeploymentBuildInput
        {
            RequestId = "dreq_1",
            CapabilityId = "deploy.azure-vm-iis",
            Route = "deploy_azure_vm_iis",
            PrincipalId = "principal_deployer",
            TaskId = "task_deploy",
            SessionId = "session_deploy",
            TargetId = deploymentTarget.TargetId,
            ProjectPath = verifyProjectDirectory
        });
        AssertTrue(deploymentBuild.Success && deploymentBuild.Request != null, "deployment builder resolves target and project file");
        AssertTrue(deploymentBuild.Request!.ProjectFile.EndsWith("Broker.Verify.csproj", StringComparison.OrdinalIgnoreCase), "deployment builder resolves the unique project file from directory");

        var deploymentHealthChecks = new AzureIisDeploymentHealthCheckService(new HttpClient(new FakeHttpMessageHandler((request, cancellationToken) =>
        {
            if (request.RequestUri?.AbsoluteUri == "https://deploy.example.com/health")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("healthy", Encoding.UTF8, "text/plain")
                });
            }

            if (request.RequestUri?.AbsoluteUri == "https://subapp.example.com/apps/verify/health")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("subapp healthy", Encoding.UTF8, "text/plain")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing", Encoding.UTF8, "text/plain")
            });
        })));
        var deploymentPreviewService = new AzureIisDeploymentPreviewService(deploymentBuilder, deploymentHealthChecks);
        var deploymentPreview = deploymentPreviewService.Preview("deploy.azure-vm-iis", new AzureIisDeploymentBuildInput
        {
            RequestId = "dreq_preview",
            CapabilityId = "deploy.azure-vm-iis",
            Route = "deploy_azure_vm_iis",
            PrincipalId = "principal_deployer",
            TaskId = "task_deploy",
            SessionId = "session_deploy",
            TargetId = deploymentTarget.TargetId,
            ProjectPath = verifyProjectDirectory
        });
        AssertTrue(deploymentPreview.Success && deploymentPreview.Result != null, "deployment preview builds dry-run deployment result");
        AssertTrue(deploymentPreview.Result!.ScriptPreview.Contains("New-PSSession", StringComparison.Ordinal), "deployment preview renders winrm powershell script");
        AssertTrue(deploymentPreview.Result!.DetailsJson.Contains("dotnet publish", StringComparison.Ordinal), "deployment preview includes dotnet publish command");
        AssertTrue(deploymentPreview.Result!.DetailsJson.Contains("https://deploy.example.com/health", StringComparison.Ordinal), "deployment preview includes computed health check url");

        var deploymentSubAppBuild = deploymentBuilder.TryBuild("deploy.azure-vm-iis", new AzureIisDeploymentBuildInput
        {
            RequestId = "dreq_subapp",
            CapabilityId = "deploy.azure-vm-iis",
            Route = "deploy_azure_vm_iis",
            PrincipalId = "principal_deployer",
            TaskId = "task_deploy",
            SessionId = "session_deploy",
            TargetId = deploymentSubAppTarget.TargetId,
            ProjectPath = verifyProjectDirectory
        });
        AssertTrue(deploymentSubAppBuild.Success && deploymentSubAppBuild.Request != null, "deployment builder supports iis child-application targets");
        AssertTrue(deploymentSubAppBuild.Request!.DeploymentMode == "iis_application", "deployment builder preserves child-application mode");
        AssertTrue(deploymentSubAppBuild.Request!.ApplicationPath == "/apps/verify", "deployment builder normalizes child-application path");

        var deploymentSubAppPreview = deploymentPreviewService.Preview("deploy.azure-vm-iis", new AzureIisDeploymentBuildInput
        {
            RequestId = "dreq_preview_subapp",
            CapabilityId = "deploy.azure-vm-iis",
            Route = "deploy_azure_vm_iis",
            PrincipalId = "principal_deployer",
            TaskId = "task_deploy",
            SessionId = "session_deploy",
            TargetId = deploymentSubAppTarget.TargetId,
            ProjectPath = verifyProjectDirectory
        });
        AssertTrue(deploymentSubAppPreview.Success && deploymentSubAppPreview.Result != null, "deployment preview supports iis child-application targets");
        AssertTrue(deploymentSubAppPreview.Result!.ScriptPreview.Contains("New-WebApplication", StringComparison.Ordinal), "deployment preview creates or updates IIS child application");
        AssertTrue(deploymentSubAppPreview.Result!.DetailsJson.Contains("\"DeploymentMode\":\"iis_application\"", StringComparison.OrdinalIgnoreCase) ||
                   deploymentSubAppPreview.Result!.DetailsJson.Contains("\"deploymentmode\":\"iis_application\"", StringComparison.OrdinalIgnoreCase),
            "deployment preview details include child-application mode");
        AssertTrue(deploymentSubAppPreview.Result!.DetailsJson.Contains("https://subapp.example.com/apps/verify/health", StringComparison.Ordinal), "deployment preview includes child-application health check url");

        var fakeProcessRunner = new FakeProcessRunner();
        var executionService = new AzureIisDeploymentExecutionService(
            deploymentBuilder,
            new FakeAzureIisDeploymentSecretResolver(),
            fakeProcessRunner,
            deploymentHealthChecks,
            sharedContextService,
            toolSpecDb);
        var dryRunExecution = await executionService.ExecuteAsync(
            "deploy.azure-vm-iis",
            new AzureIisDeploymentBuildInput
            {
                RequestId = "dreq_exec_dry",
                CapabilityId = "deploy.azure-vm-iis",
                Route = "deploy_azure_vm_iis",
                PrincipalId = "principal_deployer",
                TaskId = "task_deploy",
                SessionId = "session_deploy",
                TargetId = deploymentTarget.TargetId,
                ProjectPath = verifyProjectDirectory
            },
            dryRun: true);
        AssertTrue(dryRunExecution.Success && dryRunExecution.Result != null, "deployment execution service supports dry-run package preparation");
        AssertTrue(dryRunExecution.Result!.Stage == "dry_run", "deployment execution service marks dry-run stage");
        AssertTrue(File.Exists(dryRunExecution.Result.PackagePath), "deployment execution service produces zip package during dry run");
        AssertTrue(fakeProcessRunner.Invocations.Count == 1 && fakeProcessRunner.Invocations[0].FileName == "dotnet", "deployment execution dry-run only invokes dotnet publish");

        var executeProcessRunner = new FakeProcessRunner();
        var actualExecutionService = new AzureIisDeploymentExecutionService(
            deploymentBuilder,
            new FakeAzureIisDeploymentSecretResolver(),
            executeProcessRunner,
            deploymentHealthChecks,
            sharedContextService,
            toolSpecDb);
        var actualExecution = await actualExecutionService.ExecuteAsync(
            "deploy.azure-vm-iis",
            new AzureIisDeploymentBuildInput
            {
                RequestId = "dreq_exec_live",
                CapabilityId = "deploy.azure-vm-iis",
                Route = "deploy_azure_vm_iis",
                PrincipalId = "principal_deployer",
                TaskId = "task_deploy",
                SessionId = "session_deploy",
                TargetId = deploymentTarget.TargetId,
                ProjectPath = verifyProjectDirectory
            },
            dryRun: false);
        AssertTrue(actualExecution.Success && actualExecution.Result != null, "deployment execution service completes with resolved secret and fake runner");
        AssertTrue(actualExecution.Result!.Stage == "deployed", "deployment execution service marks deployed stage");
        AssertTrue(executeProcessRunner.Invocations.Count == 2 && executeProcessRunner.Invocations[1].FileName == "powershell", "deployment execution invokes remote powershell after publish");
        AssertTrue(actualExecution.Result!.DetailsJson.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase), "deployment execution records successful health check");
        var recentDeployments = actualExecutionService.ListRecentExecutions();
        AssertTrue(recentDeployments.Count > 0 && recentDeployments[0].DocumentId == "deployment.execution.dreq_exec_live", "deployment execution service lists recent deployment evidence");
        var deploymentEvidence = actualExecutionService.ReadExecution("deployment.execution.dreq_exec_live");
        AssertTrue(deploymentEvidence != null && deploymentEvidence.TargetId == deploymentTarget.TargetId, "deployment execution service reads deployment evidence detail");
        AssertTrue(deploymentEvidence != null && deploymentEvidence.HealthCheckUrl == "https://deploy.example.com/health", "deployment execution evidence preserves health check url");

        var googleCredentialPath = Path.Combine(sandboxRoot, "google-service-account.json");
        using (var rsa = RSA.Create(2048))
        {
            var serviceAccountJson = JsonSerializer.Serialize(new
            {
                type = "service_account",
                project_id = "verify-project",
                private_key_id = "verify-key",
                private_key = rsa.ExportPkcs8PrivateKeyPem(),
                client_email = "verify@test.invalid",
                client_id = "1234567890",
                token_uri = "https://oauth2.googleapis.com/token"
            });
            File.WriteAllText(googleCredentialPath, serviceAccountJson, Encoding.UTF8);
        }

        var googleDriveTempFile = Path.Combine(sandboxRoot, "drive-delivery-test.txt");
        File.WriteAllText(googleDriveTempFile, "drive delivery verify", Encoding.UTF8);
        var googleDriveClient = new HttpClient(new FakeGoogleDriveHandler());
        var googleOAuthClientPath = Path.Combine(sandboxRoot, "google-oauth-client.json");
        var googleOAuthClientJson = """
        {
          "installed": {
            "client_id": "verify-client-id",
            "project_id": "verify-project",
            "auth_uri": "https://accounts.google.com/o/oauth2/auth",
            "token_uri": "https://oauth2.googleapis.com/token",
            "client_secret": "verify-client-secret"
          }
        }
        """;
        File.WriteAllText(googleOAuthClientPath, googleOAuthClientJson, Encoding.UTF8);
        var googleDriveOptions = new GoogleDriveDeliveryOptions
        {
            ServiceAccountJsonPath = googleCredentialPath,
            OAuthClientJsonPath = googleOAuthClientPath,
            DefaultFolderId = "folder_verify",
            DefaultShareMode = "anyone_with_link",
            DefaultIdentityMode = "shared_delegated",
            SharedDelegatedChannel = "line",
            SharedDelegatedUserId = "shared_owner",
            DelegatedRedirectUri = "http://localhost:5361/api/v1/google-drive/oauth/callback"
        };
        using var googleOAuthDb = BrokerDb.UseSqlite($"Data Source={Path.Combine(sandboxRoot, "google-oauth.db")}");
        var googleOAuthInitializer = new BrokerDbInitializer(googleOAuthDb);
        googleOAuthInitializer.Initialize();
        var googleOAuthService = new GoogleDriveOAuthService(
            googleOAuthDb,
            googleDriveOptions,
            googleDriveClient,
            NullLogger<GoogleDriveOAuthService>.Instance);
        var googleDriveService = new GoogleDriveShareService(
            googleDriveOptions,
            googleOAuthService,
            googleDriveClient,
            NullLogger<GoogleDriveShareService>.Instance);
        var googleDriveStatus = googleDriveService.GetStatus();
        AssertTrue(googleDriveStatus.Enabled, "google drive delivery status reports enabled when credential path and folder id are configured");
        AssertTrue(googleDriveStatus.HasOAuthClientFile, "google drive delivery status reports oauth client when configured");
        var googleDriveShare = await googleDriveService.ShareFileAsync(new GoogleDriveShareRequest
        {
            FilePath = googleDriveTempFile,
            IdentityMode = "system_account"
        });
        AssertTrue(googleDriveShare.Success, $"google drive delivery uploads file through service account flow :: {googleDriveShare.Message}");
        AssertTrue(googleDriveShare.FileId == "drive_file_verify", "google drive delivery preserves uploaded file id");
        AssertTrue(googleDriveShare.WebViewLink.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase), "google drive delivery returns web view link");
        AssertTrue(googleDriveShare.DownloadLink.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase), "google drive delivery returns download link");
        var oauthStatus = googleOAuthService.GetStatus();
        AssertTrue(oauthStatus.HasOAuthClientFile, "google oauth status reports client file when configured");
        AssertTrue(oauthStatus.RedirectUri == "http://localhost:5361/api/v1/google-drive/oauth/callback", "google oauth status preserves redirect uri");
        var oauthStart = googleOAuthService.StartAuthorization("line", "verify_user");
        AssertTrue(oauthStart.AuthorizationUrl.Contains("verify-client-id", StringComparison.Ordinal), "google oauth start includes client id");
        AssertTrue(oauthStart.AuthorizationUrl.Contains("drive.file", StringComparison.Ordinal), "google oauth start requests drive.file scope");
        var oauthCallback = await googleOAuthService.CompleteAuthorizationAsync(
            VerifyUrlHelpers.ExtractQueryParam(oauthStart.AuthorizationUrl, "state"),
            "verify-auth-code",
            null);
        AssertTrue(oauthCallback.Success, $"google oauth callback succeeds with fake token exchange :: {oauthCallback.Message}");
        AssertTrue(oauthCallback.GoogleEmail == "verify.user@example.com", "google oauth callback resolves google email");
        var delegatedToken = await googleOAuthService.GetDelegatedAccessTokenAsync("line", "verify_user");
        AssertTrue(delegatedToken == "verify_delegated_access_token", "google oauth service refreshes delegated access token");
        var delegatedShare = await googleDriveService.ShareFileAsync(new GoogleDriveShareRequest
        {
            FilePath = googleDriveTempFile,
            IdentityMode = "user_delegated",
            Channel = "line",
            UserId = "verify_user"
        });
        AssertTrue(delegatedShare.Success, $"google drive delivery uploads file through delegated oauth flow :: {delegatedShare.Message}");
        var sharedOwnerStart = googleOAuthService.StartAuthorization("line", "shared_owner");
        var sharedOwnerCallback = await googleOAuthService.CompleteAuthorizationAsync(
            VerifyUrlHelpers.ExtractQueryParam(sharedOwnerStart.AuthorizationUrl, "state"),
            "verify-auth-code-shared",
            null);
        AssertTrue(sharedOwnerCallback.Success, "google oauth callback succeeds for shared delegated owner");
        var sharedDelegatedShare = await googleDriveService.ShareFileAsync(new GoogleDriveShareRequest
        {
            FilePath = googleDriveTempFile,
            IdentityMode = "shared_delegated",
            Channel = "line",
            UserId = "line_any_user"
        });
        AssertTrue(sharedDelegatedShare.Success, $"google drive delivery uploads file through shared delegated oauth flow :: {sharedDelegatedShare.Message}");
    }

    var localAdminDbPath = Path.Combine(sandboxRoot, "local-admin.db");
    using (var localAdminDb = BrokerDb.UseSqlite($"Data Source={localAdminDbPath}"))
    {
        var initializer = new BrokerDbInitializer(localAdminDb);
        initializer.Initialize();

        var localAdmin = new LocalAdminAuthService(localAdminDb, NullLogger<LocalAdminAuthService>.Instance);
        var initialContext = CreateLocalHttpContext();
        var initialStatus = localAdmin.GetStatus(initialContext);
        AssertTrue(initialStatus.LocalRequest, "local admin status recognizes localhost requests");
        AssertTrue(!initialStatus.HasPassword && initialStatus.InitialPasswordActive, "local admin status starts with initial password mode");

        var firstLogin = localAdmin.Login(initialContext, "admin", "AdminPass#2026");
        AssertTrue(firstLogin.Authenticated && !firstLogin.RequiresPasswordChange, "local admin accepts initial password only when new password is supplied");

        var sessionCookie = ExtractCookieValue(initialContext, LocalAdminAuthService.SessionCookieName);
        var authenticatedContext = CreateLocalHttpContext(sessionCookie);
        var authenticatedStatus = localAdmin.GetStatus(authenticatedContext);
        AssertTrue(authenticatedStatus.Authenticated, "local admin status recognizes authenticated session cookie");
        AssertTrue(localAdmin.TryRequireAuthenticated(authenticatedContext, out _, out _), "local admin gate accepts authenticated localhost session");

        var changedPassword = localAdmin.ChangePassword(authenticatedContext, "AdminPass#2026", "AdminPass#2026B");
        AssertTrue(changedPassword.Authenticated && changedPassword.Message.Contains("updated", StringComparison.OrdinalIgnoreCase), "local admin can change password after login");

        localAdmin.Logout(authenticatedContext);
        var loggedOutContext = CreateLocalHttpContext(sessionCookie);
        AssertTrue(!localAdmin.TryRequireAuthenticated(loggedOutContext, out _, out _), "local admin gate rejects revoked session after logout");

        var oldPasswordLogin = localAdmin.Login(CreateLocalHttpContext(), "AdminPass#2026", null);
        AssertTrue(!oldPasswordLogin.Authenticated, "local admin rejects the old password after password change");

        var secondLoginContext = CreateLocalHttpContext();
        var secondLogin = localAdmin.Login(secondLoginContext, "AdminPass#2026B", null);
        AssertTrue(secondLogin.Authenticated, "local admin accepts the updated password on subsequent login");
    }

    var logDbPath = Path.Combine(sandboxRoot, "interaction-log.db");
    using (var logDb = BrokerDb.UseSqlite($"Data Source={logDbPath}"))
    {
        var initializer = new BrokerDbInitializer(logDb);
        initializer.Initialize();
        var recorder = new HighLevelInteractionRecorder(logDb);
        recorder.Record(new HighLevelInteractionRecord
        {
            Channel = "line",
            UserId = "tester",
            RawInput = "/build website",
            RawReply = "project name required",
            ParsedKind = HighLevelInputKind.Production.ToString(),
            WorkflowState = HighLevelWorkflowState.Idle.ToString(),
            WorkflowAction = HighLevelWorkflowAction.StartProduction.ToString(),
            RouteMode = HighLevelRouteMode.Production.ToString()
        });
        recorder.Record(new HighLevelInteractionRecord
        {
            Channel = "line",
            UserId = "tester",
            RawInput = "#MySite",
            RawReply = "draft updated",
            ParsedKind = HighLevelInputKind.ProjectName.ToString(),
            WorkflowState = HighLevelWorkflowState.AwaitingProjectName.ToString(),
            WorkflowAction = HighLevelWorkflowAction.CaptureProjectName.ToString(),
            RouteMode = HighLevelRouteMode.Production.ToString()
        });

        var logEntries = recorder.ReadLatest("line", "tester", 10);
        AssertTrue(logEntries.Count == 2, "interaction recorder persists append-only records by channel and user");
        AssertTrue(logEntries[0].RawInput == "/build website", "interaction recorder preserves raw input");
        AssertTrue(logEntries[1].WorkflowAction == HighLevelWorkflowAction.CaptureProjectName.ToString(), "interaction recorder preserves interpreted workflow action");

        var memoryStore = new HighLevelMemoryStore(logDb);
        memoryStore.Write(new HighLevelMemoryState
        {
            Channel = "line",
            UserId = "tester",
            CurrentGoal = "build website",
            LastRouteMode = HighLevelRouteMode.Production.ToString(),
            WorkflowState = HighLevelWorkflowState.AwaitingProjectName.ToString(),
            WorkflowAction = HighLevelWorkflowAction.CaptureProjectName.ToString(),
            PendingDraftId = "draft_123",
            PendingProjectName = true,
            LastTaskType = "code_gen"
        });

        var memoryState = memoryStore.ReadLatest("line", "tester");
        AssertTrue(memoryState != null, "memory store persists projected memory state");
        AssertTrue(memoryState!.CurrentGoal == "build website", "memory store keeps de-commanded current goal");
        AssertTrue(memoryState.PendingDraftId == "draft_123", "memory store preserves reusable workflow state");

        var interpretationStore = new HighLevelInterpretationStore(logDb);
        interpretationStore.Record(new HighLevelInterpretationRecord
        {
            Channel = "line",
            UserId = "tester",
            InteractionType = HighLevelRouteMode.Production.ToString(),
            ParsedKind = HighLevelInputKind.Production.ToString(),
            WorkflowState = HighLevelWorkflowState.Idle.ToString(),
            WorkflowAction = HighLevelWorkflowAction.StartProduction.ToString(),
            CommandExtractionAllowed = true,
            TrustReason = "raw user input may issue commands",
            CandidateGoal = "build website",
            TaskType = "code_gen",
            DraftId = "draft_123"
        });

        var interpretations = interpretationStore.ReadLatest("line", "tester", 10);
        AssertTrue(interpretations.Count == 1, "interpretation store persists append-only interpreted records");
        AssertTrue(interpretations[0].CandidateGoal == "build website", "interpretation store keeps candidate goal separate from raw log");

        var executionIntentStore = new HighLevelExecutionIntentStore(logDb);
        executionIntentStore.Write(new HighLevelExecutionIntent
        {
            Channel = "line",
            UserId = "tester",
            Stage = "executable",
            PromotionReason = "explicit confirm",
            Goal = "build website",
            TaskType = "code_gen",
            ProjectName = "MySite",
            DraftId = "draft_123"
        });

        var executionIntent = executionIntentStore.ReadLatest("line", "tester");
        AssertTrue(executionIntent != null, "execution intent store persists promoted executable intent");
        AssertTrue(executionIntent!.Stage == "executable", "execution intent store preserves promotion stage");
        AssertTrue(HighLevelExecutionIntentStore.BuildDocumentId("line", "tester") == "hlm.execution.line.tester", "execution intent document id is stable for downstream references");
    }

    var legalRagDbPath = Path.Combine(sandboxRoot, "legal-rag.db");
    using (var legalRagDb = BrokerDb.UseSqlite($"Data Source={legalRagDbPath}"))
    {
        var initializer = new BrokerDbInitializer(legalRagDb);
        initializer.Initialize();

        var legalTaskId = "legal-rag-verify";
        var legalEmbedding = new FakeLegalEmbeddingService();
        var legalRagPipeline = new RagPipelineService(new RagPipelineOptions
        {
            QueryRewriteEnabled = false,
            RerankEnabled = false,
            CacheEnabled = false
        });
        var legalRagDispatcher = new InProcessDispatcher(
            NullLogger<InProcessDispatcher>.Instance,
            sandboxRoot,
            db: legalRagDb,
            embeddingService: legalEmbedding,
            ragPipeline: legalRagPipeline);

        var legalFixtureJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                key = "第19條",
                content = "通訊交易或訪問交易之消費者，得於收受商品或接受服務後七日內，以退回商品或書面通知方式解除契約。",
                tag = "消費者保護法",
                tags = new[] { "消費者保護法", "法律", "退貨" }
            },
            new
            {
                key = "第2條",
                content = "本法所稱消費者，指以消費為目的而為交易、使用商品或接受服務者。",
                tag = "消費者保護法",
                tags = new[] { "消費者保護法", "法律", "定義" }
            },
            new
            {
                key = "咖啡規範",
                content = "咖啡拉花教學資料，不屬於消費者保護法條文。",
                tag = "餐飲內規",
                tags = new[] { "餐飲內規" }
            }
        });

        var importPayload = JsonSerializer.Serialize(new
        {
            route = "rag_import",
            args = new
            {
                format = "json",
                tag = "法律",
                data = legalFixtureJson,
                task_id = legalTaskId
            }
        });
        var importResult = await legalRagDispatcher.DispatchAsync(new ApprovedRequest
        {
            RequestId = "req_legal_rag_import",
            CapabilityId = "rag.import",
            Route = "rag_import",
            Payload = importPayload,
            TaskId = legalTaskId
        });
        AssertTrue(importResult.Success, $"legal RAG import route succeeds :: {importResult.ErrorMessage}");

        var legalEntries = legalRagDb.GetAll<SharedContextEntry>()
            .Where(e => e.TaskId == legalTaskId)
            .ToList();
        AssertTrue(legalEntries.Any(e => e.Key == "消費者保護法:第19條" && e.ContentRef.Contains("七日內", StringComparison.Ordinal)), "legal RAG externalizes Consumer Protection Act article state");

        var legalFtsRows = legalRagDb.Query<LegalRagFtsRow>(
            "SELECT source_key, content FROM memory_fts WHERE memory_fts MATCH @q AND task_id = @taskId ORDER BY rank LIMIT 5",
            new { q = InProcessDispatcher.PrepareFts5Query("通訊交易 七日 解除契約"), taskId = legalTaskId });
        AssertTrue(legalFtsRows.Any(row => row.SourceKey == "消費者保護法:第19條"), "legal RAG indexes Consumer Protection Act article in FTS5");

        var fulltextPayload = JsonSerializer.Serialize(new
        {
            route = "rag_retrieve",
            args = new
            {
                query = "通訊交易 七日 解除契約",
                mode = "fulltext",
                limit = 3,
                rewrite = false,
                rerank = false,
                tags = new[] { "消費者保護法" }
            }
        });
        var fulltextResult = await legalRagDispatcher.DispatchAsync(new ApprovedRequest
        {
            RequestId = "req_legal_rag_fulltext",
            CapabilityId = "rag.retrieve",
            Route = "rag_retrieve",
            Payload = fulltextPayload,
            TaskId = legalTaskId
        });
        AssertTrue(fulltextResult.Success, $"legal RAG fulltext retrieve succeeds :: {fulltextResult.ErrorMessage}");
        using (var fulltextDoc = JsonDocument.Parse(fulltextResult.ResultPayload ?? "{}"))
        {
            var keys = fulltextDoc.RootElement.GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("key").GetString() ?? "")
                .ToList();
            AssertTrue(keys.FirstOrDefault() == "消費者保護法:第19條", "legal RAG fulltext retrieve returns the relevant law article first");
        }

        var semanticPayload = JsonSerializer.Serialize(new
        {
            route = "rag_retrieve",
            args = new
            {
                query = "七日退貨解除契約",
                mode = "semantic",
                limit = 3,
                threshold = 0.5,
                rewrite = false,
                rerank = false,
                tags = new[] { "消費者保護法" }
            }
        });
        var semanticResult = await legalRagDispatcher.DispatchAsync(new ApprovedRequest
        {
            RequestId = "req_legal_rag_semantic",
            CapabilityId = "rag.retrieve",
            Route = "rag_retrieve",
            Payload = semanticPayload,
            TaskId = legalTaskId
        });
        AssertTrue(semanticResult.Success, $"legal RAG semantic retrieve succeeds :: {semanticResult.ErrorMessage}");
        using (var semanticDoc = JsonDocument.Parse(semanticResult.ResultPayload ?? "{}"))
        {
            var first = semanticDoc.RootElement.GetProperty("results").EnumerateArray().FirstOrDefault();
            AssertTrue(first.ValueKind == JsonValueKind.Object && first.GetProperty("key").GetString() == "消費者保護法:第19條", "legal RAG vector retrieve returns the relevant law article");
            AssertTrue(first.GetProperty("vector_score").GetSingle() > 0, "legal RAG vector retrieve reports a positive vector score");
        }

        var filteredPayload = JsonSerializer.Serialize(new
        {
            route = "rag_retrieve",
            args = new
            {
                query = "咖啡 拉花",
                mode = "fulltext",
                limit = 3,
                rewrite = false,
                rerank = false,
                tags = new[] { "消費者保護法" }
            }
        });
        var filteredResult = await legalRagDispatcher.DispatchAsync(new ApprovedRequest
        {
            RequestId = "req_legal_rag_tag_filter",
            CapabilityId = "rag.retrieve",
            Route = "rag_retrieve",
            Payload = filteredPayload,
            TaskId = legalTaskId
        });
        AssertTrue(filteredResult.Success, $"legal RAG tag-filtered retrieve succeeds :: {filteredResult.ErrorMessage}");
        using (var filteredDoc = JsonDocument.Parse(filteredResult.ResultPayload ?? "{}"))
        {
            var filteredKeys = filteredDoc.RootElement.GetProperty("results")
                .EnumerateArray()
                .Select(item => item.GetProperty("key").GetString() ?? "")
                .ToList();
            AssertTrue(!filteredKeys.Any(key => key.StartsWith("餐飲內規:", StringComparison.Ordinal)), "legal RAG tag filter excludes unrelated non-law entries");
        }
    }

    var coordinatorDbPath = Path.Combine(sandboxRoot, "coordinator-profile.db");
    using (var coordinatorDb = BrokerDb.UseSqlite($"Data Source={coordinatorDbPath}"))
    {
        var initializer = new BrokerDbInitializer(coordinatorDb);
        initializer.Initialize();

        var queryMediator = new HighLevelQueryToolMediator(
            new FakeToolSpecRegistry(),
            new FakeExecutionDispatcher(),
            NullLogger<HighLevelQueryToolMediator>.Instance);

        var lineGateway = new LineChatGateway(
            new LineChatGatewayOptions
            {
                Enabled = true,
                RagEnabled = false,
                SystemPrompt = "verify"
            },
            new HighLevelLlmOptions
            {
                Provider = "ollama",
                BaseUrl = "http://localhost:11434",
                DefaultModel = "verify"
            },
            new FakeHttpClientFactory(),
            coordinatorDb,
            new EmbeddingService(new EmbeddingOptions { Enabled = false }),
            new RagPipelineService(new RagPipelineOptions
            {
                QueryRewriteEnabled = false,
                RerankEnabled = false,
                CacheEnabled = false
            }),
            NullLogger<LineChatGateway>.Instance);
        var coordinatorGoogleOAuthClientPath = Path.Combine(sandboxRoot, "coordinator-google-oauth-client.json");
        File.WriteAllText(
            coordinatorGoogleOAuthClientPath,
            """
            {
              "installed": {
                "client_id": "verify-client-id",
                "project_id": "verify-project",
                "auth_uri": "https://accounts.google.com/o/oauth2/auth",
                "token_uri": "https://oauth2.googleapis.com/token",
                "client_secret": "verify-client-secret"
              }
            }
            """,
            Encoding.UTF8);
        var coordinatorGoogleDriveClient = new HttpClient(new FakeGoogleDriveHandler());
        var coordinatorGoogleDriveOptions = new GoogleDriveDeliveryOptions
        {
            OAuthClientJsonPath = coordinatorGoogleOAuthClientPath,
            DefaultFolderId = "folder_verify",
            DefaultShareMode = "anyone_with_link",
            DefaultIdentityMode = "shared_delegated",
            SharedDelegatedChannel = "line",
            SharedDelegatedUserId = "line-drive-owner",
            DelegatedRedirectUri = "http://localhost:5361/api/v1/google-drive/oauth/callback"
        };
        var coordinatorGoogleOAuthService = new GoogleDriveOAuthService(
            coordinatorDb,
            coordinatorGoogleDriveOptions,
            coordinatorGoogleDriveClient,
            NullLogger<GoogleDriveOAuthService>.Instance);
        var coordinatorGoogleDriveService = new GoogleDriveShareService(
            coordinatorGoogleDriveOptions,
            coordinatorGoogleOAuthService,
            coordinatorGoogleDriveClient,
            NullLogger<GoogleDriveShareService>.Instance);
        var coordinatorWorkspaceService = new HighLevelLineWorkspaceService(
            coordinatorDb,
            new HighLevelCoordinatorOptions
            {
                AccessRoot = Path.Combine(sandboxRoot, "managed"),
                CommandGuideReminderMinutes = 60
            });
        var coordinatorDownloadOptions = new BrokerArtifactDownloadOptions
        {
            SigningSecret = "verify-artifact-download-secret",
            SidecarLastTunnelUrlPath = Path.Combine(sandboxRoot, ".last-tunnel-url")
        };
        var coordinatorDownloadService = new BrokerArtifactDownloadService(
            coordinatorWorkspaceService,
            new SidecarPublicUrlResolver(coordinatorDownloadOptions),
            coordinatorDownloadOptions);
        var coordinatorArtifactDeliveryService = new LineArtifactDeliveryService(
            coordinatorWorkspaceService,
            coordinatorGoogleDriveService,
            coordinatorDownloadService,
            NullLogger<LineArtifactDeliveryService>.Instance);
        var coordinatorScaffoldSpecStore = new HighLevelSystemScaffoldSpecStore(coordinatorDb);
        var coordinatorScaffoldIterationStore = new HighLevelSystemScaffoldIterationStore(coordinatorDb);
        var coordinatorScaffoldProgressStore = new HighLevelSystemScaffoldProgressStore(coordinatorDb);
        var coordinatorDocumentArtifactService = new HighLevelDocumentArtifactService(
            new HighLevelLlmOptions
            {
                Provider = "ollama",
                BaseUrl = "http://localhost:11434",
                DefaultModel = "verify"
            },
            coordinatorGoogleDriveService,
            coordinatorGoogleOAuthService,
            coordinatorArtifactDeliveryService,
            new FakeHttpClientFactory(),
            NullLogger<HighLevelDocumentArtifactService>.Instance);
        var coordinatorCodeArtifactService = new HighLevelCodeArtifactService(
            new HighLevelLlmOptions
            {
                Provider = "ollama",
                BaseUrl = "http://localhost:11434",
                DefaultModel = "verify"
            },
            coordinatorGoogleDriveService,
            coordinatorArtifactDeliveryService,
            new FakeHttpClientFactory(),
            NullLogger<HighLevelCodeArtifactService>.Instance);
        var coordinatorSystemScaffoldService = new HighLevelSystemScaffoldService(
            coordinatorWorkspaceService,
            coordinatorArtifactDeliveryService,
            coordinatorScaffoldSpecStore,
            coordinatorScaffoldIterationStore,
            coordinatorScaffoldProgressStore,
            NullLogger<HighLevelSystemScaffoldService>.Instance);
        var coordinatorSiteRebuildService = new HighLevelSiteRebuildService(
            coordinatorWorkspaceService,
            coordinatorArtifactDeliveryService,
            new FakeSiteRebuildPageFetcher(),
            NullLogger<HighLevelSiteRebuildService>.Instance);
        var coordinatorSharedDriveAuth = coordinatorGoogleOAuthService.StartAuthorization("line", "line-drive-owner");
        var coordinatorSharedDriveCallback = await coordinatorGoogleOAuthService.CompleteAuthorizationAsync(
            VerifyUrlHelpers.ExtractQueryParam(coordinatorSharedDriveAuth.AuthorizationUrl, "state"),
            "verify-auth-code-shared-owner",
            null);
        AssertTrue(coordinatorSharedDriveCallback.Success, "coordinator google oauth callback succeeds for shared drive owner");
        var coordinatorTemplateCatalogRoot = Path.Combine(sandboxRoot, "coordinator-template-catalog");
        Directory.CreateDirectory(Path.Combine(coordinatorTemplateCatalogRoot, "member_portal"));
        File.WriteAllText(
            Path.Combine(coordinatorTemplateCatalogRoot, "catalog.json"),
            """
            {
              "families": [
                {
                  "template_id": "member_portal",
                  "supported_project_scales": ["mini_app", "structured_app"],
                  "manifest_path": "./member_portal/template.manifest.json"
                }
              ]
            }
            """);
        File.WriteAllText(
            Path.Combine(coordinatorTemplateCatalogRoot, "member_portal", "template.manifest.json"),
            """
            {
              "template_id": "member_portal",
              "title": "Member Portal",
              "summary": "Protected portal with login and member content.",
              "supported_project_scales": ["mini_app", "structured_app"],
              "required_sections": ["auth_gate"],
              "optional_modules": ["profile"],
              "supported_styles": ["clean_enterprise"],
              "supported_component_sets": ["core"]
            }
            """);
        var coordinatorProjectInterviewStateMachine = new ProjectInterviewStateMachine();
        var coordinatorProjectInterviewStateService = new ProjectInterviewStateService(coordinatorDb);
        var coordinatorProjectInterviewRestatementService = new ProjectInterviewRestatementService();
        var coordinatorProjectInterviewTemplateCatalogService = new ProjectInterviewTemplateCatalogService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ProjectInterview:TemplateCatalogPath"] = Path.Combine(coordinatorTemplateCatalogRoot, "catalog.json")
                })
                .Build(),
            new FakeWebHostEnvironment(sandboxRoot));
        var coordinatorProjectInterviewCompiler = new ProjectInterviewProjectDefinitionCompiler();
        var coordinatorProjectInterviewWorkflowDesignService = new ProjectInterviewWorkflowDesignService();
        var coordinatorProjectInterviewPdfRenderService = new ProjectInterviewPdfRenderService();

        var toggleNya = new ToggleNyaChatOrchestrator();
        var coordinator = new HighLevelCoordinator(
            coordinatorDb,
            new FakeBrokerService(),
            new FakePlanService(),
            new FakeTaskRouter(),
            lineGateway,
            toggleNya,
            queryMediator,
            new HighLevelRelationQueryService(
                queryMediator,
                new HighLevelLlmOptions
                {
                    Provider = "ollama",
                    BaseUrl = "http://localhost:11434",
                    DefaultModel = "verify"
                },
                new FakeHttpClientFactory(),
                NullLogger<HighLevelRelationQueryService>.Instance),
            new HighLevelCoordinatorOptions
            {
                AccessRoot = Path.Combine(sandboxRoot, "managed"),
                CommandGuideReminderMinutes = 60
            },
            new FakeHighLevelExecutionModelPlanner(),
            coordinatorDocumentArtifactService,
            coordinatorCodeArtifactService,
            coordinatorSystemScaffoldService,
            coordinatorSiteRebuildService,
            coordinatorArtifactDeliveryService,
            new BrowserBindingService(coordinatorDb),
            coordinatorProjectInterviewStateMachine,
            coordinatorProjectInterviewStateService,
            coordinatorProjectInterviewRestatementService,
            coordinatorProjectInterviewTemplateCatalogService,
            coordinatorProjectInterviewCompiler,
            coordinatorProjectInterviewWorkflowDesignService,
            coordinatorProjectInterviewPdfRenderService,
            NullLogger<HighLevelCoordinator>.Instance);

        var setName = await coordinator.ProcessLineMessageAsync("line-user-a", "/name 小布");
        AssertTrue(setName.Error == null, "coordinator accepts explicit display-name command");
        var profileAfterName = coordinator.GetLineUserProfile("line-user-a");
        AssertTrue(profileAfterName?.PreferredDisplayName == "小布", "coordinator persists preferred display name");

        var setId = await coordinator.ProcessLineMessageAsync("line-user-a", "/id bricks001");
        AssertTrue(setId.Error == null, "coordinator accepts explicit alphanumeric user id command");
        var profileAfterId = coordinator.GetLineUserProfile("line-user-a");
        AssertTrue(profileAfterId?.PreferredUserCode == "bricks001", "coordinator persists preferred alphanumeric user id");

        var profileView = await coordinator.ProcessLineMessageAsync("line-user-a", "?profile");
        AssertTrue(profileView.Reply.Contains("display_name: 小布", StringComparison.Ordinal), "profile query shows preferred display name");
        AssertTrue(profileView.Reply.Contains("user_code: bricks001", StringComparison.Ordinal), "profile query shows preferred alphanumeric user id");
        AssertTrue(profileView.Reply.Contains("目前擁有的權限：", StringComparison.Ordinal), "profile query shows current permission summary");
        AssertTrue(profileView.Reply.Contains("交通查詢：?rail、?hsr、?bus、?flight", StringComparison.Ordinal), "profile query lists transport query permissions");
        var verifyAccessRoot = Path.Combine(sandboxRoot, "managed");
        AssertTrue(!profileView.Reply.Contains(verifyAccessRoot, StringComparison.OrdinalIgnoreCase), "profile query does not expose the host workspace path");
        AssertTrue(profileView.Reply.Contains("workspace: line/bricks001", StringComparison.Ordinal), "profile query shows the workspace as a relative name");

        var updatedPermissions = coordinator.SetLineUserPermissions("line-user-a", new HighLevelUserPermissionsPatch
        {
            AllowQuery = false,
            AllowTransport = false,
            AllowProduction = false
        });
        AssertTrue(updatedPermissions?.Permissions.AllowQuery == false, "coordinator persists per-user query permission updates");
        AssertTrue(updatedPermissions?.Permissions.AllowTransport == false, "coordinator persists per-user transport permission updates");
        AssertTrue(updatedPermissions?.Permissions.AllowProduction == false, "coordinator persists per-user production permission updates");

        var deniedSearch = await coordinator.ProcessLineMessageAsync("line-user-a", "?search 中央氣象署官網");
        AssertTrue(deniedSearch.Error == "query_disabled", "coordinator denies explicit search when query permission is disabled");

        var deniedTransport = await coordinator.ProcessLineMessageAsync("line-user-a", "?hsr 台北 台中 今天 18:00");
        AssertTrue(deniedTransport.Error == "transport_disabled", "coordinator denies explicit transport query when transport permission is disabled");

        var deniedProduction = await coordinator.ProcessLineMessageAsync("line-user-a", "/build website prototype");
        AssertTrue(deniedProduction.Error == "production_disabled", "coordinator denies production command when production permission is disabled");

        coordinator.SetLineUserPermissions("line-user-a", new HighLevelUserPermissionsPatch
        {
            AllowQuery = true,
            AllowTransport = true,
            AllowProduction = true
        });

        var basicStillDenied = await coordinator.ProcessLineMessageAsync("line-user-a", "/build website prototype");
        AssertTrue(basicStillDenied.Error == "production_disabled", "basic tier still masks production even when raw production flag is true");
        var basicInterviewDenied = await coordinator.ProcessLineMessageAsync("line-user-a", "/proj");
        AssertTrue(basicInterviewDenied.Error == "production_disabled", "basic tier cannot start a /proj interview");
        var basicApproveDenied = await coordinator.ProcessLineMessageAsync("line-user-a", "/ok");
        AssertTrue(basicApproveDenied.Error == "production_disabled", "basic tier cannot approve a /proj design");

        var promotedUser = coordinator.ReviewLineUserRegistration("line-user-a", "promote", "verify production access");
        AssertTrue(promotedUser?.AccessTier == HighLevelAccessTier.Member, "registration review promotes user to member tier");

        var buildDraft = await coordinator.ProcessLineMessageAsync("line-user-a", "/build website prototype");
        AssertTrue(buildDraft.Draft != null, "production command still creates draft after profile customization");
        AssertTrue(buildDraft.Draft!.ManagedPaths.UserRoot.Contains("bricks001", StringComparison.OrdinalIgnoreCase), "managed paths use preferred alphanumeric user id");

        var projectNamed = await coordinator.ProcessLineMessageAsync("line-user-a", "#VerifySite");
        AssertTrue(projectNamed.Draft?.ProjectName == "VerifySite", "project-name command updates production draft");
        coordinator.ReviewLineUserRegistration("line-user-a", "demote", "verify demotion before confirmation");
        var demotedConfirm = await coordinator.ProcessLineMessageAsync("line-user-a", "confirm");
        AssertTrue(demotedConfirm.Error == "production_disabled" && demotedConfirm.CreatedTask == null, "a user demoted after the draft cannot confirm it");
        AssertTrue(coordinator.GetLineDraft("line-user-a") != null, "the draft stays when confirmation is denied");
        coordinator.ReviewLineUserRegistration("line-user-a", "promote", "verify production access");
        var confirmedDraft = await coordinator.ProcessLineMessageAsync("line-user-a", "confirm");
        AssertTrue(confirmedDraft.CreatedTask != null, "confirm creates broker task");
        using var runtimeDescriptorDoc = JsonDocument.Parse(confirmedDraft.CreatedTask!.RuntimeDescriptor);
        AssertTrue(runtimeDescriptorDoc.RootElement.TryGetProperty("requested_execution_model", out var requestedModelNode), "runtime descriptor includes requested execution model");
        AssertTrue(requestedModelNode.GetProperty("alias").GetString() == "execution-strong", "runtime descriptor preserves planner-requested model alias");
        AssertTrue(runtimeDescriptorDoc.RootElement.TryGetProperty("llm", out var llmNode), "runtime descriptor includes forwarded llm overrides");
        AssertTrue(llmNode.GetProperty("default_model").GetString() == "verify-strong-model", "runtime descriptor forwards validated execution model");
        var workflowAdmin = new HighLevelWorkflowAdminService(coordinatorDb);
        var workflowSnapshot = workflowAdmin.GetSnapshot();
        AssertTrue(workflowSnapshot.ExecutionIntents.Count > 0, "workflow admin service lists recent execution intents");
        AssertTrue(workflowSnapshot.Handoffs.Count > 0, "workflow admin service lists recent handoffs");
        var latestIntentDoc = workflowSnapshot.ExecutionIntents.First(item => item.UserId == "line-user-a").DocumentId;
        var latestIntent = workflowAdmin.ReadExecutionIntent(latestIntentDoc);
        AssertTrue(latestIntent != null && latestIntent.TaskType == "code_gen", "workflow admin service reads execution intent detail");
        var latestHandoffDoc = workflowSnapshot.Handoffs.First().DocumentId;
        var latestHandoff = workflowAdmin.ReadHandoff(latestHandoffDoc);
        AssertTrue(latestHandoff != null && latestHandoff.TaskType == "code_gen", "workflow admin service reads handoff detail");

        async Task PromoteLineUserForProductionAsync(string productionUserId)
        {
            await coordinator.ProcessLineMessageAsync(productionUserId, "hello");
            var productionPermissions = coordinator.SetLineUserPermissions(productionUserId, new HighLevelUserPermissionsPatch
            {
                AllowProduction = true
            });
            AssertTrue(productionPermissions?.Permissions.AllowProduction == true, $"raw production permission enabled for {productionUserId}");
            var review = coordinator.ReviewLineUserRegistration(productionUserId, "promote", "verify production access");
            AssertTrue(review?.AccessTier == HighLevelAccessTier.Member, $"member tier enabled for {productionUserId}");
        }

        await PromoteLineUserForProductionAsync("line-inline-project-user");
        var inlineProjectDraft = await coordinator.ProcessLineMessageAsync("line-inline-project-user", "/建立 單頁基礎計算機網頁 #proj1");
        AssertTrue(inlineProjectDraft.Draft != null && inlineProjectDraft.Draft.TaskType == "code_gen", "inline project-name website command still resolves to code_gen draft");
        AssertTrue(inlineProjectDraft.Draft!.ProjectName == "proj1", "inline project-name token is captured into the draft");
        AssertTrue(inlineProjectDraft.FollowUpMessages != null && inlineProjectDraft.FollowUpMessages.Contains("y"), "inline project-name draft exposes short confirm follow-up");
        var inlineProjectRoot = inlineProjectDraft.Draft.ManagedPaths.ProjectRoot;
        var inlineProjectConfirmed = await coordinator.ProcessLineMessageAsync("line-inline-project-user", "confirm");
        AssertTrue(inlineProjectConfirmed.CreatedTask != null && inlineProjectConfirmed.CreatedTask.TaskType == "code_gen", "confirm creates broker task for inline project-name code_gen draft");
        AssertTrue(!string.IsNullOrWhiteSpace(inlineProjectConfirmed.Reply), "code_gen confirm reply is not empty");
        AssertTrue(inlineProjectConfirmed.Reply.Contains("已生成網站原型", StringComparison.Ordinal), "code_gen confirm reply reports generated website prototype");
        AssertTrue(!inlineProjectConfirmed.Reply.Contains("目前擁有的權限", StringComparison.Ordinal), "code_gen confirm reply no longer appends the full command guide");
        AssertTrue(!inlineProjectConfirmed.Reply.Contains(verifyAccessRoot, StringComparison.OrdinalIgnoreCase), "code_gen confirm reply does not expose host paths");
        AssertTrue(inlineProjectConfirmed.Reply.Contains("project_folder: proj1", StringComparison.Ordinal), "code_gen confirm reply names only the project folder");
        AssertTrue(!string.IsNullOrWhiteSpace(inlineProjectRoot), "code_gen draft captures project root");
        AssertTrue(File.Exists(Path.Combine(inlineProjectRoot, "index.html")), "code_gen confirm writes index.html into project root");
        var generatedSiteContent = File.ReadAllText(Path.Combine(inlineProjectRoot, "index.html"), Encoding.UTF8);
        AssertTrue(generatedSiteContent.Contains("verify", StringComparison.OrdinalIgnoreCase) || generatedSiteContent.Contains("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase), "code_gen project root contains generated HTML content");
        var codeArtifacts = coordinatorWorkspaceService.ListArtifacts("line-inline-project-user");
        AssertTrue(codeArtifacts.Any(item => item.RelatedTaskType == "code_gen"), "code_gen confirm records artifact metadata");

        await PromoteLineUserForProductionAsync("line-doc-user");
        var docDraft = await coordinator.ProcessLineMessageAsync("line-doc-user", "/請整理成 markdown 文件，摘要目前進度");
        var resolvedDocDraft = docDraft.Draft ?? throw new Exception("doc_gen draft unexpectedly null");
        AssertTrue(resolvedDocDraft.TaskType == "doc_gen", "document production command creates doc_gen draft");
        AssertTrue(!resolvedDocDraft.RequiresProjectName, "doc_gen draft does not require project name");
        AssertTrue(docDraft.FollowUpMessages != null && docDraft.FollowUpMessages.Contains("y"), "doc_gen draft exposes short confirm as a follow-up message");
        await PromoteLineUserForProductionAsync("line-doc-user-create");
        var createDocDraft = await coordinator.ProcessLineMessageAsync("line-doc-user-create", "/create 產生一份 markdown 文件，摘要目前進度");
        AssertTrue(createDocDraft.Draft != null && createDocDraft.Draft.TaskType == "doc_gen", "create production alias still resolves to doc_gen draft");
        var docConfirmed = await coordinator.ProcessLineMessageAsync("line-doc-user", "confirm");
        AssertTrue(docConfirmed.CreatedTask != null && docConfirmed.CreatedTask.TaskType == "doc_gen", "confirm creates broker task for doc_gen draft");
        AssertTrue(docConfirmed.Reply.Contains("已生成", StringComparison.Ordinal), "doc_gen confirm reply includes artifact delivery summary");
        AssertTrue(!docConfirmed.Reply.Contains("目前擁有的權限", StringComparison.Ordinal), "doc_gen confirm reply no longer appends the full command guide");
        var docManagedPaths = coordinator.GetLineManagedPaths("line-doc-user");
        AssertTrue(docManagedPaths != null, "doc_gen user managed paths can be resolved");
        var docDocumentsRoot = docManagedPaths is null ? throw new Exception("doc_gen managed paths unexpectedly null") : docManagedPaths.DocumentsRoot;
        var generatedDocFiles = Directory.GetFiles(docDocumentsRoot, "*.md", SearchOption.TopDirectoryOnly);
        AssertTrue(generatedDocFiles.Length > 0, "doc_gen confirm writes a markdown artifact into user documents root");
        var generatedDocContent = File.ReadAllText(generatedDocFiles[0], Encoding.UTF8);
        AssertTrue(generatedDocContent.Contains("verify-reply", StringComparison.Ordinal), "doc_gen artifact preserves generated UTF-8 content");
        var recordedArtifacts = coordinatorWorkspaceService.ListArtifacts("line-doc-user");
        AssertTrue(recordedArtifacts.Count > 0, "doc_gen confirm records artifact metadata");
        AssertTrue(recordedArtifacts[0].DeliveryMode == "google_drive", "doc_gen artifact record preserves google drive delivery mode when shared delegated credential is available");
        AssertTrue(recordedArtifacts[0].DriveIdentityMode == "shared_delegated", "doc_gen artifact record preserves shared delegated drive identity mode");
        var recordedArtifact = coordinatorWorkspaceService.ReadArtifact($"hlm.artifact.line.line-doc-user.{recordedArtifacts[0].ArtifactId}");
        AssertTrue(recordedArtifact != null && recordedArtifact.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase), "artifact detail can be read by document id");

        await PromoteLineUserForProductionAsync("line-scaffold-user");
        var scaffoldDraft = await coordinator.ProcessLineMessageAsync("line-scaffold-user", "/建立 完整系統雛形 #scaffoldproj");
        AssertTrue(scaffoldDraft.Draft != null && scaffoldDraft.Draft.TaskType == "system_scaffold", "system scaffold command creates system_scaffold draft");
        AssertTrue(scaffoldDraft.Draft!.ProjectName == "scaffoldproj", "system scaffold draft captures inline project name");
        AssertTrue(scaffoldDraft.Reply.Contains("scaffold_family:", StringComparison.Ordinal), "system scaffold draft reply includes structured scaffold summary");
        AssertTrue(scaffoldDraft.Reply.Contains("ui_components: custom_component_library", StringComparison.Ordinal), "system scaffold draft defaults to custom component library");
        AssertTrue(scaffoldDraft.FollowUpMessages != null && scaffoldDraft.FollowUpMessages.Contains("y"), "system scaffold draft exposes short confirm follow-up");

        var scaffoldRefined = await coordinator.ProcessLineMessageAsync("line-scaffold-user", "需要登入、SQLite 與 Azure IIS 佈署");
        AssertTrue(scaffoldRefined.Draft != null && scaffoldRefined.Draft.TaskType == "system_scaffold", "system scaffold refinement keeps the pending scaffold draft");
        AssertTrue(scaffoldRefined.Reply.Contains("auth: required", StringComparison.Ordinal), "system scaffold refinement updates auth requirement");
        AssertTrue(scaffoldRefined.Reply.Contains("database: sqlite", StringComparison.Ordinal), "system scaffold refinement updates database requirement");

        var scaffoldConfirmed = await coordinator.ProcessLineMessageAsync("line-scaffold-user", "confirm");
        AssertTrue(scaffoldConfirmed.CreatedTask != null && scaffoldConfirmed.CreatedTask.TaskType == "system_scaffold", "confirm creates broker task for system scaffold draft");
        AssertTrue(scaffoldConfirmed.Reply.Contains("已生成並封裝系統雛形", StringComparison.Ordinal), "system scaffold confirm reply reports packaged scaffold generation");
        AssertTrue(!scaffoldConfirmed.Reply.Contains(verifyAccessRoot, StringComparison.OrdinalIgnoreCase), "system scaffold confirm reply does not expose host paths");
        AssertTrue(scaffoldConfirmed.Reply.Contains("package_file: scaffoldproj-scaffold.zip", StringComparison.Ordinal), "system scaffold confirm reply names only the package file");
        var scaffoldPaths = coordinator.GetLineManagedPaths("line-scaffold-user");
        AssertTrue(scaffoldPaths != null, "system scaffold managed paths can be resolved");
        var scaffoldProjectRoot = scaffoldPaths is null ? throw new Exception("system scaffold managed paths unexpectedly null") : Path.Combine(scaffoldPaths.ProjectsRoot, "scaffoldproj");
        AssertTrue(File.Exists(Path.Combine(scaffoldProjectRoot, "frontend", "index.html")), "system scaffold writes frontend scaffold files");
        AssertTrue(File.Exists(Path.Combine(scaffoldProjectRoot, "docs", "requirements-analysis.md")), "system scaffold writes requirements analysis document");
        var scaffoldDesignPlan = File.ReadAllText(Path.Combine(scaffoldProjectRoot, "docs", "design-plan.md"), Encoding.UTF8);
        AssertTrue(scaffoldDesignPlan.Contains("UI Components: custom_component_library", StringComparison.Ordinal), "system scaffold design plan records custom component library strategy");
        var scaffoldArtifacts = coordinatorWorkspaceService.ListArtifacts("line-scaffold-user");
        AssertTrue(scaffoldArtifacts.Any(item => item.RelatedTaskType == "system_scaffold" && item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)), "system scaffold confirm records packaged zip artifact");
        AssertTrue(scaffoldConfirmed.FollowUpMessages != null && scaffoldConfirmed.FollowUpMessages.Any(item => item.Contains("進度：", StringComparison.Ordinal)), "system scaffold confirm returns phase progress follow-up messages");

        // /proj 訪談：/ok 只建立 system_scaffold draft，回 y 之後才走 ConfirmDraft 建立 task 與 handoff
        await PromoteLineUserForProductionAsync("line-interview-user");
        var interviewStart = await coordinator.ProcessLineMessageAsync("line-interview-user", "/proj");
        AssertTrue(interviewStart.Error == null && interviewStart.Reply.Contains("專案訪談已開始", StringComparison.Ordinal), "member tier can start a /proj interview");
        await coordinator.ProcessLineMessageAsync("line-interview-user", "#VerifyInterview");
        await coordinator.ProcessLineMessageAsync("line-interview-user", "2");
        var interviewReview = await coordinator.ProcessLineMessageAsync("line-interview-user", "1");
        AssertTrue(interviewReview.Reply.Contains("/ok", StringComparison.Ordinal), "project interview reaches review with /ok guidance");
        var interviewApproved = await coordinator.ProcessLineMessageAsync("line-interview-user", "/ok");
        AssertTrue(interviewApproved.Draft?.TaskType == "system_scaffold", "/ok creates a system_scaffold draft");
        AssertTrue(interviewApproved.CreatedTask == null, "/ok does not create a task before confirmation");
        AssertTrue(!Directory.Exists(interviewApproved.Draft!.ManagedPaths.ProjectRoot), "/ok does not build the project before confirmation");
        AssertTrue(interviewApproved.FollowUpMessages != null && interviewApproved.FollowUpMessages.Contains("y"), "/ok draft exposes the y confirm follow-up");
        var approvedInterview = await coordinatorProjectInterviewStateService.LoadTaskDocumentAsync("line", "line-interview-user", CancellationToken.None);
        AssertTrue(approvedInterview.SessionState.CurrentPhase == ProjectInterviewPhase.AwaitBuildConfirmation, "/ok leaves the interview awaiting build confirmation");
        var interviewConfirmed = await coordinator.ProcessLineMessageAsync("line-interview-user", "y");
        AssertTrue(interviewConfirmed.CreatedTask?.TaskType == "system_scaffold", "confirming the /ok draft creates a system_scaffold task");
        AssertTrue(interviewConfirmed.Handoff != null, "confirming the /ok draft writes a handoff");
        AssertTrue(interviewConfirmed.Reply.Contains("已生成並封裝系統雛形", StringComparison.Ordinal), "confirming the /ok draft builds through the legacy scaffold path");
        AssertTrue(!interviewConfirmed.Reply.Contains(verifyAccessRoot, StringComparison.OrdinalIgnoreCase), "interview build reply does not expose host paths");
        var builtInterview = await coordinatorProjectInterviewStateService.LoadTaskDocumentAsync("line", "line-interview-user", CancellationToken.None);
        AssertTrue(builtInterview.SessionState.CurrentPhase == ProjectInterviewPhase.Confirmed, "the interview is confirmed once its build task exists");

        // Governed：確認 system_scaffold draft 後改由受控代理經生成能力產出；broker 程序內不寫出檔案，
        // 立即回覆「已受理」。前置條件不滿足時 fail-closed，不建立任務也不退回程序內生成。
        {
            var governedOutputRoot = Path.Combine(sandboxRoot, "generation-out");
            Directory.CreateDirectory(governedOutputRoot);
            var governedContainers = new VerifyRecordingContainerManager();
            var governedReadiness = new VerifyGovernedReadiness();
            var governedSpawnService = new AgentSpawnService(coordinatorDb);
            using var governedCrypto = new BrokerCore.Crypto.EnvelopeCrypto();
            var governedLauncher = new GovernedGenerationLauncher(
                new GovernedGenerationOptions { OutputRoot = governedOutputRoot },
                governedReadiness,
                new AgentContainerLauncher(
                    governedSpawnService,
                    governedContainers,
                    governedCrypto,
                    new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["FunctionPool:ContainerManager:AgentBrokerUrl"] = "http://host.containers.internal:5361"
                        })
                        .Build(),
                    new HighLevelLlmOptions { DefaultModel = "verify" },
                    new RegistrationCredentialOptions()),
                governedSpawnService,
                new GovernedGenerationRunStore(coordinatorDb),
                coordinatorDb,
                NullLogger<GovernedGenerationLauncher>.Instance);
            var governedCoordinator = new HighLevelCoordinator(
                coordinatorDb,
                new FakeBrokerService(),
                new FakePlanService(),
                new FakeTaskRouter(),
                lineGateway,
                null,
                queryMediator,
                new HighLevelRelationQueryService(
                    queryMediator,
                    new HighLevelLlmOptions
                    {
                        Provider = "ollama",
                        BaseUrl = "http://localhost:11434",
                        DefaultModel = "verify"
                    },
                    new FakeHttpClientFactory(),
                    NullLogger<HighLevelRelationQueryService>.Instance),
                new HighLevelCoordinatorOptions
                {
                    AccessRoot = Path.Combine(sandboxRoot, "managed"),
                    CommandGuideReminderMinutes = 60,
                    Generation = new HighLevelGenerationOptions { SystemScaffoldMode = SystemScaffoldModes.Governed }
                },
                new FakeHighLevelExecutionModelPlanner(),
                coordinatorDocumentArtifactService,
                coordinatorCodeArtifactService,
                coordinatorSystemScaffoldService,
                coordinatorSiteRebuildService,
                coordinatorArtifactDeliveryService,
                new BrowserBindingService(coordinatorDb),
                coordinatorProjectInterviewStateMachine,
                coordinatorProjectInterviewStateService,
                coordinatorProjectInterviewRestatementService,
                coordinatorProjectInterviewTemplateCatalogService,
                coordinatorProjectInterviewCompiler,
                coordinatorProjectInterviewWorkflowDesignService,
                coordinatorProjectInterviewPdfRenderService,
                NullLogger<HighLevelCoordinator>.Instance,
                governedGenerationLauncher: governedLauncher);

            await PromoteLineUserForProductionAsync("line-governed-user");
            var governedDraft = await governedCoordinator.ProcessLineMessageAsync("line-governed-user", "/建立 完整系統雛形 #governedproj");
            AssertTrue(governedDraft.Draft?.TaskType == "system_scaffold", "governed mode still creates a system_scaffold draft");

            governedReadiness.Ready = false;
            var governedUnavailable = await governedCoordinator.ProcessLineMessageAsync("line-governed-user", "y");
            AssertTrue(governedUnavailable.Error == "generation_unavailable", "governed mode fails closed when its prerequisites are missing");
            AssertTrue(governedUnavailable.CreatedTask == null, "governed mode creates no task when it is not ready");
            AssertTrue(governedUnavailable.Reply.Contains("暫不可用", StringComparison.Ordinal), "governed mode tells the user generation is unavailable");
            AssertTrue(governedContainers.Spawned.Count == 0, "governed mode starts no agent when it is not ready");

            governedReadiness.Ready = true;
            var governedConfirmed = await governedCoordinator.ProcessLineMessageAsync("line-governed-user", "y");
            AssertTrue(governedConfirmed.Error == null, "governed confirm succeeds once ready");
            AssertTrue(governedConfirmed.CreatedTask?.TaskType == "system_scaffold", "governed confirm creates a system_scaffold task");
            AssertTrue(governedConfirmed.Reply.Contains("已受理", StringComparison.Ordinal), "governed confirm replies that the request was accepted");
            AssertTrue(governedConfirmed.Reply.Contains(governedConfirmed.CreatedTask!.TaskId, StringComparison.Ordinal), "governed confirm reply names the task id");
            AssertTrue(!governedConfirmed.Reply.Contains("已生成並封裝系統雛形", StringComparison.Ordinal), "governed confirm does not run the in-process scaffold");
            AssertTrue(!governedConfirmed.Reply.Contains(verifyAccessRoot, StringComparison.OrdinalIgnoreCase), "governed confirm reply does not expose host paths");
            AssertTrue(governedConfirmed.CreatedTask.AssignedRoleId == "role_executor", "governed task is assigned to role_executor");
            AssertTrue(governedConfirmed.CreatedTask.AssignedPrincipalId == GovernedGenerationLauncher.BuildPrincipalId(governedConfirmed.CreatedTask.TaskId), "governed task is assigned to its own AI principal");
            AssertTrue(TaskRuntimeDescriptor.Parse(governedConfirmed.CreatedTask.RuntimeDescriptor).CapabilityGrants.Count == 3, "governed task carries the three generation grants");
            AssertTrue(governedConfirmed.Handoff?.GenerationRequest != null, "governed handoff carries the sanitized generation request");
            AssertTrue(governedContainers.Spawned.Count == 1 && governedContainers.Spawned[0].TrustedEnvironment.ContainsKey("AGENT_RUN"), "governed confirm starts one agent with a work item");
            AssertTrue(governedContainers.Spawned[0].SecretEnvironment.ContainsKey(AgentContainerLauncher.RegistrationSecretEnvironmentVariable), "governed agent receives its registration credential as a secret");
            var governedPaths = governedCoordinator.GetLineManagedPaths("line-governed-user")!;
            var governedProjectRoot = Path.Combine(governedPaths.ProjectsRoot, "governedproj");
            AssertTrue(!Directory.Exists(governedProjectRoot) || !Directory.EnumerateFileSystemEntries(governedProjectRoot, "*", SearchOption.AllDirectories).Any(), "governed confirm writes no project files in the broker process");
            AssertTrue(!Directory.Exists(governedPaths.DocumentsRoot) || !Directory.EnumerateFiles(governedPaths.DocumentsRoot).Any(), "governed confirm writes no package in the broker process");
            AssertTrue(governedCoordinator.GetLineDraft("line-governed-user") == null, "governed confirm removes the draft once the agent started");
        }

        await PromoteLineUserForProductionAsync("line-site-rebuild-user");
        var siteRebuildDraft = await coordinator.ProcessLineMessageAsync("line-site-rebuild-user", "/重製網站 https://example.edu/ 深度3 #sitecopy");
        AssertTrue(siteRebuildDraft.Draft != null && siteRebuildDraft.Draft.TaskType == "site_rebuild", "site rebuild command with URL creates site_rebuild draft");
        AssertTrue(siteRebuildDraft.Draft!.ProjectName == "sitecopy", "site rebuild draft captures inline project name");
        AssertTrue(siteRebuildDraft.FollowUpMessages != null && siteRebuildDraft.FollowUpMessages.Contains("y"), "site rebuild draft exposes confirm follow-up");

        var siteRebuildConfirmed = await coordinator.ProcessLineMessageAsync("line-site-rebuild-user", "confirm");
        AssertTrue(siteRebuildConfirmed.CreatedTask != null && siteRebuildConfirmed.CreatedTask.TaskType == "site_rebuild", "confirm creates broker task for site_rebuild draft");
        AssertTrue(siteRebuildConfirmed.Reply.Contains("已重製網站並封裝成可下載套件", StringComparison.Ordinal), "site rebuild confirm reply reports packaged site generation");
        AssertTrue(siteRebuildConfirmed.Reply.Contains("Google Drive download link:", StringComparison.Ordinal), "site rebuild confirm reply includes drive download label");
        AssertTrue(siteRebuildConfirmed.Reply.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase), "site rebuild confirm reply includes drive link");
        var siteRebuildArtifacts = coordinatorWorkspaceService.ListArtifacts("line-site-rebuild-user");
        var siteRebuildArtifact = siteRebuildArtifacts.Single(item => item.RelatedTaskType == "site_rebuild");
        AssertTrue(siteRebuildArtifact.UploadedToGoogleDrive, "site rebuild artifact records google drive upload");
        AssertTrue(File.Exists(siteRebuildArtifact.FilePath), "site rebuild zip package exists on disk");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(siteRebuildArtifact.FilePath))
        {
            AssertTrue(zip.Entries.Any(entry => entry.FullName == "index.html"), "site rebuild zip contains index.html");
            AssertTrue(zip.Entries.Any(entry => entry.FullName == "site.json"), "site rebuild zip contains site.json");
            AssertTrue(zip.Entries.Any(entry => entry.FullName == "components/manifest.json"), "site rebuild zip contains component manifest");
        }

        var duplicateId = await coordinator.ProcessLineMessageAsync("line-user-b", "/id bricks001");
        AssertTrue(duplicateId.Error == "invalid_user_code", "coordinator rejects duplicate preferred user id");

        var realLineConversation = await coordinator.ProcessLineMessageAsync("U1234567890abcdef1234567890abcdef", "hello");
        AssertTrue(!string.IsNullOrWhiteSpace(realLineConversation.Reply), "real line-style user can create profile");

        var listedUsers = coordinator.ListLineUsers();
        var testUserSummary = listedUsers.Single(user => user.UserId == "line-user-a");
        AssertTrue(testUserSummary.IsTestAccount, "synthetic line user is marked as test account");
        AssertTrue(testUserSummary.AccountType == "test", "synthetic line user exposes test account type");

        var realUserSummary = listedUsers.Single(user => user.UserId == "U1234567890abcdef1234567890abcdef");
        AssertTrue(!realUserSummary.IsTestAccount, "real line-style user is not marked as test account");
        AssertTrue(realUserSummary.AccountType == "line_user", "real line-style user exposes line_user account type");

        var firstConversation = await coordinator.ProcessLineMessageAsync("line-user-c", "你好");
        AssertTrue(firstConversation.Reply.Contains("目前擁有的權限：", StringComparison.Ordinal), "first interaction guide shows current permission summary");

        var querySuggestion = await coordinator.ProcessLineMessageAsync("line-user-d", "?中央氣象署官網");
        AssertTrue(querySuggestion.FollowUpMessages != null && querySuggestion.FollowUpMessages.Any(item => item.Contains("?search", StringComparison.Ordinal)), "generic high-level query prompts controlled search as a follow-up command");

        var railSuggestion = await coordinator.ProcessLineMessageAsync("line-user-e", "台北到台中最晚高鐵班次");
        AssertTrue(railSuggestion.FollowUpMessages != null && railSuggestion.FollowUpMessages.Any(item => item.Contains("?hsr", StringComparison.Ordinal)), "lookup-style transport conversation prompts controlled hsr search as a follow-up command");

        toggleNya.Enabled = true;
        var nyaLookup = await coordinator.ProcessLineMessageAsync("line-user-e", "我住哪裡你知道嗎？");
        AssertTrue(nyaLookup.Reply == "nya-verify-reply" && toggleNya.Calls == 1, "lookup-style conversation goes to NyaChat while NyaChat is enabled");
        toggleNya.Enabled = false;
        var restoredSuggestion = await coordinator.ProcessLineMessageAsync("line-user-e", "台北到台中最晚高鐵班次");
        AssertTrue(toggleNya.Calls == 1 && restoredSuggestion.FollowUpMessages != null && restoredSuggestion.FollowUpMessages.Any(item => item.Contains("?hsr", StringComparison.Ordinal)), "lookup-style suggestion returns once NyaChat is disabled");

        var explicitSearchReply = await coordinator.ProcessLineMessageAsync("line-user-f", "?search 中央氣象署官網");
        AssertTrue(explicitSearchReply.Reply.Contains("verify-reply", StringComparison.OrdinalIgnoreCase), "explicit search uses high-level model to synthesize broker search results");
    }

    var readmePath = Path.Combine(sandboxRoot, "README.txt");
    File.WriteAllText(readmePath, "hello broker");

    var policy = new PolicyEngine(new SchemaValidator(), new PolicyEngineOptions());
    var capability = new Capability
    {
        CapabilityId = "file.read",
        Route = "read_file",
        ActionType = ActionType.Read,
        ResourceType = "file",
        RiskLevel = RiskLevel.Low,
        ApprovalPolicy = "auto",
        ParamSchema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string" }
            },
            required = new[] { "path" }
        })
    };

    var grant = new CapabilityGrant
    {
        CapabilityId = "file.read",
        ScopeOverride = JsonSerializer.Serialize(new
        {
            paths = new[] { sandboxRoot },
            routes = new[] { "read_file" }
        }),
        RemainingQuota = -1,
        ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        Status = GrantStatus.Active
    };

    var task = new BrokerTask
    {
        TaskId = "task_verify",
        TaskType = "analysis",
        SubmittedBy = "tester",
        ScopeDescriptor = "{}",
        State = TaskState.Active,
        RiskLevel = RiskLevel.Low
    };

    var allowed = new ExecutionRequest
    {
        RequestPayload = JsonSerializer.Serialize(new
        {
            route = "read_file",
            args = new { path = "README.txt" },
            project_root = sandboxRoot
        })
    };

    var allowedResult = policy.Evaluate(allowed, capability, grant, task, 1, 1);
    AssertTrue(allowedResult.Decision == PolicyDecision.Allow, "policy allows matching capability + route + path scope");

    var wrongRoute = new ExecutionRequest
    {
        RequestPayload = JsonSerializer.Serialize(new
        {
            route = "list_directory",
            args = new { path = "README.txt" },
            project_root = sandboxRoot
        })
    };

    var wrongRouteResult = policy.Evaluate(wrongRoute, capability, grant, task, 1, 1);
    AssertTrue(wrongRouteResult.Decision == PolicyDecision.Deny, "policy denies payload route outside capability route");

    var wrongPath = new ExecutionRequest
    {
        RequestPayload = JsonSerializer.Serialize(new
        {
            route = "read_file",
            args = new { path = "../secret.txt" },
            project_root = sandboxRoot
        })
    };

    var wrongPathResult = policy.Evaluate(wrongPath, capability, grant, task, 1, 1);
    AssertTrue(wrongPathResult.Decision == PolicyDecision.Deny, "policy denies path outside granted path scope");

    var missingPath = new ExecutionRequest
    {
        RequestPayload = JsonSerializer.Serialize(new
        {
            route = "read_file",
            args = new { },
            project_root = sandboxRoot
        })
    };

    var missingPathResult = policy.Evaluate(missingPath, capability, grant, task, 1, 1);
    AssertTrue(missingPathResult.Decision == PolicyDecision.Deny, "policy validates args schema instead of wrapper payload");

    var dispatcher = new InProcessDispatcher(NullLogger<InProcessDispatcher>.Instance, sandboxRoot);

    var readResult = await dispatcher.DispatchAsync(new BrokerCore.Contracts.ApprovedRequest
    {
        RequestId = "req_read",
        Route = "read_file",
        Payload = JsonSerializer.Serialize(new
        {
            route = "read_file",
            args = new { path = "README.txt" }
        })
    });
    AssertTrue(readResult.Success, "dispatcher reads file from canonical route+args payload");

    var searchResult = await dispatcher.DispatchAsync(new BrokerCore.Contracts.ApprovedRequest
    {
        RequestId = "req_search",
        Route = "search_content",
        Payload = JsonSerializer.Serialize(new
        {
            route = "search_content",
            args = new
            {
                directory = ".",
                pattern = "hello",
                file_pattern = "*.txt"
            }
        })
    });
    AssertTrue(searchResult.Success, "dispatcher searches content from canonical route+args payload");

    var routeMismatchResult = await dispatcher.DispatchAsync(new BrokerCore.Contracts.ApprovedRequest
    {
        RequestId = "req_bad_route",
        Route = "read_file",
        Payload = JsonSerializer.Serialize(new
        {
            route = "list_directory",
            args = new { path = "README.txt" }
        })
    });
    AssertTrue(!routeMismatchResult.Success, "dispatcher denies mismatched payload route");

    // ── TDX Station Extraction Tests ──
    Console.WriteLine("\n=== TDX Station Extraction ===");

    var (s1o, s1d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractStations("台北 台中");
    AssertTrue(s1o == "台北" && s1d == "台中", $"ExtractStations(\"台北 台中\") → \"{s1o}\",\"{s1d}\"");

    var (s2o, s2d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractStations("台北到高雄");
    AssertTrue(s2o == "台北" && s2d == "高雄", $"ExtractStations(\"台北到高雄\") → \"{s2o}\",\"{s2d}\"");

    var (s3o, s3d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractStations("從板橋至台南 18:00");
    AssertTrue(s3o == "板橋" && s3d == "台南", $"ExtractStations(\"從板橋至台南 18:00\") → \"{s3o}\",\"{s3d}\"");

    var (s4o, s4d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractStations("南港→左營");
    AssertTrue(s4o == "南港" && s4d == "左營", $"ExtractStations(\"南港→左營\") → \"{s4o}\",\"{s4d}\"");

    var (s5o, s5d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractStations("台中");
    AssertTrue(s5o == null || s5d == null, $"ExtractStations(\"台中\") returns null (single station)");

    // 自然語言站名提取測試
    Console.WriteLine("\n=== TDX Natural Language Station Extraction ===");

    var (s6o, s6d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractTraStations("明天 早上 板橋 往 高雄 自強號 第一班");
    AssertTrue(s6o == "板橋" && s6d == "高雄", $"NL: \"明天 早上 板橋 往 高雄 自強號 第一班\" → \"{s6o}\",\"{s6d}\"");

    var (s7o, s7d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractThsrStations("我想搭高鐵從台北去台南下午三點");
    AssertTrue(s7o == "台北" && s7d == "台南", $"NL: \"我想搭高鐵從台北去台南下午三點\" → \"{s7o}\",\"{s7d}\"");

    var (s8o, s8d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractTraStations("新左營到花蓮的火車");
    AssertTrue(s8o == "新左營" && s8d == "花蓮", $"NL: \"新左營到花蓮的火車\" → \"{s8o}\",\"{s8d}\"");

    var (s9o, s9d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractTraStations("台北車站出發到台中火車站");
    AssertTrue(s9o == "台北" && s9d == "台中", $"NL: \"台北車站出發到台中火車站\" → \"{s9o}\",\"{s9d}\"");

    // ── TDX API Live Test (if configured) ──
    Console.WriteLine("\n=== TDX API Live Test ===");
    var tdxOpts = new Broker.Services.TdxOptions
    {
        ClientId = Environment.GetEnvironmentVariable("TDX_CLIENT_ID") ?? "",
        ClientSecret = Environment.GetEnvironmentVariable("TDX_CLIENT_SECRET") ?? ""
    };
    var tdxService = new Broker.Services.TdxApiService(
        tdxOpts,
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
        NullLogger<Broker.Services.TdxApiService>.Instance);

    if (tdxService.IsConfigured)
    {
        var token = await tdxService.GetAccessTokenAsync();
        AssertTrue(!string.IsNullOrWhiteSpace(token), "TDX token obtained successfully");

        var logger = NullLogger.Instance;
        var traResult = await Broker.Handlers.Travel.TdxTravelHelper.QueryTraTimetableAsync(
            tdxService, "台北 台中", logger, CancellationToken.None);
        AssertTrue(traResult != null, "TDX TRA query '台北 台中' returned result");

        if (traResult != null)
        {
            var traJson = JsonSerializer.Serialize(traResult);
            AssertTrue(traJson.Contains("train_count"), "TRA result contains train_count");
            AssertTrue(traJson.Contains("departure_time"), "TRA result contains departure_time");
            Console.WriteLine($"  TRA result preview: {traJson[..Math.Min(200, traJson.Length)]}...");
        }

        var hsrResult = await Broker.Handlers.Travel.TdxTravelHelper.QueryThsrTimetableAsync(
            tdxService, "台北 左營", logger, CancellationToken.None);
        AssertTrue(hsrResult != null, "TDX THSR query '台北 左營' returned result");

        if (hsrResult != null)
        {
            var hsrJson = JsonSerializer.Serialize(hsrResult);
            AssertTrue(hsrJson.Contains("train_count"), "THSR result contains train_count");
            Console.WriteLine($"  THSR result preview: {hsrJson[..Math.Min(200, hsrJson.Length)]}...");
        }

        // 航班測試
        var (fa1o, fa1d) = Broker.Handlers.Travel.TdxTravelHelper.ExtractAirports("松山飛澎湖的班機");
        AssertTrue(fa1o == "松山" && fa1d == "澎湖", $"ExtractAirports(\"松山飛澎湖的班機\") → \"{fa1o}\",\"{fa1d}\"");

        var flightResult = await Broker.Handlers.Travel.TdxTravelHelper.QueryFlightAsync(
            tdxService, "松山 金門", logger, CancellationToken.None);
        AssertTrue(flightResult != null, "TDX Flight query '松山 金門' returned result");

        if (flightResult != null)
        {
            var flightJson = JsonSerializer.Serialize(flightResult);
            AssertTrue(flightJson.Contains("flight_count"), "Flight result contains flight_count");
            Console.WriteLine($"  Flight result preview: {flightJson[..Math.Min(250, flightJson.Length)]}...");
        }
    }
    else
    {
        Console.WriteLine("  SKIP: TDX not configured");
    }

    Console.WriteLine("Broker verify passed.");
}
finally
{
    var deleted = false;
    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            Directory.Delete(sandboxRoot, recursive: true);
            deleted = true;
            break;
        }
        catch (IOException)
        {
            if (attempt < 4)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (attempt < 4)
            {
                Thread.Sleep(250 * (attempt + 1));
            }
        }
    }

    if (!deleted)
    {
        Console.WriteLine($"Warning: verify temp directory not deleted: {sandboxRoot}");
    }
}

file sealed class FakeToolSpecRegistry : IToolSpecRegistry
{
    private readonly IReadOnlyList<ToolSpecView> _views =
    [
        new ToolSpecView
        {
            ToolId = "web.search.google",
            DisplayName = "Google Web Search",
            Summary = "broker mediated search",
            Kind = "search",
            Status = "active",
            CapabilityBindings =
            [
                new ToolCapabilityBindingView
                {
                    CapabilityId = "web.search.google",
                    Route = "web_search_google",
                    Purpose = "test",
                    Registered = true,
                    RegisteredRoute = "web_search_google"
                }
            ]
        },
        new ToolSpecView
        {
            ToolId = "web.search.duckduckgo",
            DisplayName = "DuckDuckGo Web Search",
            Summary = "broker mediated search",
            Kind = "search",
            Status = "active",
            CapabilityBindings =
            [
                new ToolCapabilityBindingView
                {
                    CapabilityId = "web.search.duckduckgo",
                    Route = "web_search_duckduckgo",
                    Purpose = "test",
                    Registered = true,
                    RegisteredRoute = "web_search_duckduckgo"
                }
            ]
        },
        new ToolSpecView
        {
            ToolId = "knowledge.wikipedia.search",
            DisplayName = "Wikipedia Search",
            Summary = "broker mediated wikipedia search",
            Kind = "knowledge",
            Status = "active",
            CapabilityBindings =
            [
                new ToolCapabilityBindingView
                {
                    CapabilityId = "knowledge.wikipedia.search",
                    Route = "knowledge_wikipedia_search",
                    Purpose = "test",
                    Registered = true,
                    RegisteredRoute = "knowledge_wikipedia_search"
                }
            ]
        },
        new ToolSpecView
        {
            ToolId = "transport.query",
            DisplayName = "Transport Query",
            Summary = "broker mediated transport query",
            Kind = "travel",
            Status = "active",
            CapabilityBindings =
            [
                new ToolCapabilityBindingView
                {
                    CapabilityId = "transport.query",
                    Route = "transport_query",
                    Purpose = "test",
                    Registered = true,
                    RegisteredRoute = "transport_query"
                }
            ]
        },
    ];
    public ToolSpecView? Get(string toolId)
        => _views.FirstOrDefault(view => string.Equals(toolId, view.ToolId, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<ToolSpecView> List(string? filter = null)
        => string.IsNullOrWhiteSpace(filter)
            ? _views
            : _views.Where(view =>
                    view.ToolId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    view.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    view.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    public IReadOnlyList<ToolSpecDocument> GetDefinitions()
        => [];
}

file sealed class FakeHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(new FakeLlmHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
}

file sealed class FakeSiteRebuildPageFetcher : IPageFetcher
{
    public Task<PageFetchResult> FetchAsync(Uri uri, CancellationToken ct)
        => FetchAsync(uri, long.MaxValue, ct);

    public Task<PageFetchResult> FetchAsync(Uri uri, long maxBytes, CancellationToken ct)
    {
        var html = uri.AbsolutePath switch
        {
            "/" => """
                <!doctype html><html><head><title>Example University</title></head>
                <body><header><a href="/about">About</a><a href="/news">News</a></header>
                <main><section><h1>Example University</h1><p>Welcome to Example University.</p></section></main></body></html>
                """,
            "/about" => """
                <!doctype html><html><head><title>About</title></head>
                <body><main><section><h1>About</h1><p>About the university.</p></section></main></body></html>
                """,
            "/news" => """
                <!doctype html><html><head><title>News</title></head>
                <body><main><section><h1>News</h1><p>Latest campus news.</p></section></main></body></html>
                """,
            _ => string.Empty
        };

        return Task.FromResult(string.IsNullOrWhiteSpace(html)
            ? PageFetchResult.Fail(uri, 404, "not_found")
            : PageFetchResult.Ok(uri, 200, "text/html", html));
    }
}

file sealed class ToggleNyaChatOrchestrator : Broker.NyaChat.Abstractions.INyaChatOrchestrator
{
    public bool Enabled { get; set; }
    public int Calls { get; private set; }
    public bool IsEnabled => Enabled;

    public Task<Broker.NyaChat.Abstractions.NyaChatResult> ChatAsync(Broker.NyaChat.Abstractions.NyaChatRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new Broker.NyaChat.Abstractions.NyaChatResult { Replies = new[] { "nya-verify-reply" } });
    }
}

file sealed class FakeHighLevelExecutionModelPlanner : IHighLevelExecutionModelPlanner
{
    public Task<HighLevelExecutionModelRequest?> RecommendAsync(
        HighLevelTaskDraft draft,
        HighLevelMemoryState memory,
        CancellationToken cancellationToken = default)
        => Task.FromResult<HighLevelExecutionModelRequest?>(new HighLevelExecutionModelRequest
        {
            Alias = "execution-strong",
            Model = "verify-strong-model",
            Tier = "strong",
            Reason = $"verify planner request for {draft.TaskType}",
            RequestedBy = "high-level-entry-model",
            ValidationStatus = "validated"
        });
}

file sealed class FakeLlmHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = """
        {
          "message": {
            "content": "verify-reply"
          }
        }
        """;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });
    }
}

file sealed class VerifyGovernedReadiness : IGovernedGenerationReadiness
{
    public bool Ready { get; set; } = true;

    public Task<GovernedGenerationReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var result = new GovernedGenerationReadinessResult();
        if (!Ready)
            result.Reasons.Add("verify: not ready");
        return Task.FromResult(result);
    }
}

file sealed class VerifyRecordingContainerManager : FunctionPool.Container.IContainerManager
{
    public List<FunctionPool.Container.ContainerSpawnRequest> Spawned { get; } = new();

    public Task<string> SpawnWorkerAsync(FunctionPool.Container.ContainerSpawnRequest request, CancellationToken ct = default)
    {
        Spawned.Add(request);
        return Task.FromResult($"verify{Spawned.Count:D6}");
    }

    public Task StopWorkerAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<FunctionPool.Container.ManagedContainer>> ListManagedAsync(CancellationToken ct = default)
        => Task.FromResult(new List<FunctionPool.Container.ManagedContainer>());

    public Task<string> GetLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task<bool> IsRuntimeAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<List<FunctionPool.Container.ContainerStats>> GetStatsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<FunctionPool.Container.ContainerStats>());
}

file sealed class FakeBrokerService : IBrokerService
{
    public BrokerTask CreateTask(string submittedBy, string taskType, string scopeDescriptor, string? assignedPrincipalId = null, string? assignedRoleId = null, string? runtimeDescriptor = null)
        => new()
        {
            TaskId = "task_fake",
            TaskType = taskType,
            SubmittedBy = submittedBy,
            ScopeDescriptor = scopeDescriptor,
            RuntimeDescriptor = runtimeDescriptor ?? "{}",
            State = TaskState.Active,
            RiskLevel = RiskLevel.Low
        };

    public BrokerTask? GetTask(string taskId) => null;

    public bool CancelTask(string taskId, string cancelledBy, string reason) => true;

    public Task<ExecutionRequest> SubmitExecutionRequestAsync(string principalId, string taskId, string sessionId, string capabilityId, string intent, string requestPayload, string idempotencyKey, string traceId)
        => Task.FromResult(new ExecutionRequest
        {
            RequestId = "req_fake",
            PrincipalId = principalId,
            TaskId = taskId,
            SessionId = sessionId,
            CapabilityId = capabilityId,
            Intent = intent,
            RequestPayload = requestPayload,
            IdempotencyKey = idempotencyKey,
            TraceId = traceId
        });

    public ExecutionRequest? GetExecutionRequest(string requestId) => null;

    public IReadOnlyList<ApprovalRequest> ListPendingApprovals() => [];

    public IReadOnlyList<ApprovalRequest> ListPendingApprovalsForApprover(string approverId, bool isAdmin) => [];

    public ApprovalDetail? GetApprovalDetail(string approvalId) => null;

    public IReadOnlyList<ApprovalDetail> ListPendingApprovalDetailsForApprover(string approverId, bool isAdmin) => [];

    public Task<ExecutionRequest?> ApproveExecutionAsync(string approvalId, string approverId, string reason, bool isAdmin = false)
        => Task.FromResult<ExecutionRequest?>(null);

    public ExecutionRequest? RejectExecution(string approvalId, string approverId, string reason, bool isAdmin = false) => null;
}

file sealed class FakePlanService : IPlanService
{
    public Plan CreatePlan(string taskId, string submittedBy, string title, string? description)
        => new()
        {
            PlanId = "plan_fake",
            TaskId = taskId,
            SubmittedBy = submittedBy,
            Title = title,
            Description = description,
            State = PlanState.Draft
        };

    public Plan? GetPlan(string planId) => null;
    public bool UpdatePlanState(string planId, PlanState newState) => true;
    public PlanNode AddNode(string planId, string capabilityId, string intent, string requestPayload, string? outputContextKey, int maxRetries) => new();
    public List<PlanNode> GetNodes(string planId) => [];
    public bool UpdateNodeState(string nodeId, NodeState state, string? requestId) => true;
    public bool IncrementRetryCount(string nodeId) => true;
    public PlanEdge AddEdge(string planId, string fromNodeId, string toNodeId, EdgeType edgeType, string? contextKey, string? condition) => new();
    public List<PlanEdge> GetEdges(string planId) => [];
    public List<PlanEdge> GetIncomingEdges(string nodeId) => [];
    public (bool IsValid, string? Error) ValidateDag(string planId) => (true, null);
    public List<PlanNode> GetReadyNodes(string planId) => [];
    public Checkpoint CreateCheckpoint(string planId, string nodeId, string snapshotRef) => new();
    public List<Checkpoint> GetCheckpoints(string planId) => [];
}

file sealed class FakeTaskRouter : ITaskRouter
{
    public RiskLevel AssessRisk(string taskType, string scopeDescriptor) => RiskLevel.Low;
    public string? RecommendRole(string taskType) => "role_admin";
}

file sealed class FakeExecutionDispatcher : IExecutionDispatcher
{
    public Task<ExecutionResult> DispatchAsync(ApprovedRequest approvedRequest)
    {
        using var payloadDoc = JsonDocument.Parse(approvedRequest.Payload);
        var args = payloadDoc.RootElement.GetProperty("args");
        var query = args.TryGetProperty("query", out var queryNode)
            ? queryNode.GetString() ?? string.Empty
            : args.TryGetProperty("user_query", out var userQueryNode)
                ? userQueryNode.GetString() ?? string.Empty
                : string.Empty;
        var transportMode = args.TryGetProperty("transport_mode", out var modeNode)
            ? modeNode.GetString() ?? "auto"
            : "auto";
        object payloadObject = approvedRequest.Route switch
        {
            "web_search_google" => new
            {
                engine = "google",
                query = string.IsNullOrWhiteSpace(query) ? "taipei weather" : query,
                results = BuildGoogleResults(query)
            },
            "knowledge_wikipedia_search" => new
            {
                engine = "wikipedia",
                query = string.IsNullOrWhiteSpace(query) ? "北京市 行政區劃" : query,
                results = BuildWikipediaResults(query)
            },
            "transport_query" => BuildTransportPayload(transportMode, query),
            _ => new
            {
                engine = "duckduckgo",
                query = "taipei weather",
                results = new[]
                {
                    new
                    {
                        rank = 1,
                        title = "Taipei Weather",
                        url = "https://example.com/weather",
                        snippet = "Rain later this afternoon."
                    }
                }
            }
        };
        var payload = JsonSerializer.Serialize(payloadObject);
        return Task.FromResult(ExecutionResult.Ok(approvedRequest.RequestId, payload));
    }

    private static object BuildTransportPayload(string mode, string query)
    {
        switch (mode)
        {
            case "rail":
                return new
                {
                    resultType = "final_answer",
                    answer = "台鐵查詢：台北 → 台中（2026-04-08）\n共 1 筆，來源：TDX 台鐵時刻表 API\n查詢時間：2026-04-08T10:00:00+08:00\n\n  自強 181  18:30 發車 / 20:12 抵達",
                    normalizedQuery = new
                    {
                        transport_mode = "rail",
                        origin = "台北",
                        destination = "台中",
                        date = "2026-04-08"
                    },
                    missingFields = Array.Empty<string>(),
                    records = new[]
                    {
                        new { title = "自強 181", snippet = "18:30 發車 / 20:12 抵達" }
                    },
                    evidence = new[]
                    {
                        new { source = "TDX", kind = "transport.provider" }
                    },
                    providerMetadata = new { provider = "tdx", mode = "rail" }
                };
            case "hsr":
                return new
                {
                    resultType = "final_answer",
                    answer = "高鐵查詢：台北 → 台中（2026-04-08）\n共 1 筆，來源：TDX 高鐵時刻表 API\n查詢時間：2026-04-08T10:00:00+08:00\n\n  車次 0615  06:15 發車 / 07:02 抵達",
                    normalizedQuery = new
                    {
                        transport_mode = "hsr",
                        origin = "台北",
                        destination = "台中",
                        date = "2026-04-08"
                    },
                    missingFields = Array.Empty<string>(),
                    records = new[]
                    {
                        new { title = "車次 0615", snippet = "06:15 發車 / 07:02 抵達" }
                    },
                    evidence = new[]
                    {
                        new { source = "TDX", kind = "transport.provider" }
                    },
                    providerMetadata = new { provider = "tdx", mode = "hsr" }
                };
            case "bus":
                return new
                {
                    resultType = "final_answer",
                    answer = "公車查詢：臺北市 → 307（2026-04-08）\n共 1 筆，來源：TDX 公車預估到站 API\n查詢時間：2026-04-08T10:00:00+08:00\n\n  公車 307  捷運忠孝復興站 / 約 4 分鐘",
                    normalizedQuery = new
                    {
                        transport_mode = "bus",
                        city = "臺北市",
                        route = "307"
                    },
                    missingFields = Array.Empty<string>(),
                    records = new[]
                    {
                        new { title = "公車 307", snippet = "捷運忠孝復興站 / 約 4 分鐘" }
                    },
                    evidence = new[]
                    {
                        new { source = "TDX", kind = "transport.provider" }
                    },
                    providerMetadata = new { provider = "tdx", mode = "bus" }
                };
            case "flight":
                return new
                {
                    resultType = "range_answer",
                    answer = "目前先依較寬條件整理可用結果。\n如果你要指定日期或時段，我可以再縮小範圍。\n\n航班查詢：松山 → 金門（2026-04-08）\n共 1 筆，來源：TDX 航班即時資訊 API (FIDS)\n查詢時間：2026-04-08T10:00:00+08:00\n\n  航班 AE1271  09:15 起飛 / 10:20 抵達（準時）",
                    normalizedQuery = new
                    {
                        transport_mode = "flight",
                        origin = "松山",
                        destination = "金門"
                    },
                    missingFields = new[] { "date" },
                    rangeContext = new
                    {
                        assumptions = new[] { "date=today" },
                        scope_note = "目前先用今天可查到的航班。"
                    },
                    records = Array.Empty<object>(),
                    evidence = new[]
                    {
                        new { source = "TDX", kind = "transport.provider" }
                    },
                    providerMetadata = new { provider = "tdx", mode = "flight" }
                };
            default:
                return new
                {
                    resultType = "need_follow_up",
                    answer = "我還需要補充幾項資訊，才能繼續查詢。",
                    normalizedQuery = new { transport_mode = mode, user_query = query },
                    missingFields = new[] { "origin", "destination" },
                    followUp = new
                    {
                        question = "請再告訴我起點與終點。",
                        followUpToken = "verify-follow-up",
                        options = new[]
                        {
                            new { id = "restatement", label = "我重新描述" }
                        }
                    },
                    records = Array.Empty<object>(),
                    evidence = new[]
                    {
                        new { source = "TDX", kind = "transport.provider" }
                    },
                    providerMetadata = new { provider = "tdx", mode = mode }
                };
        }
    }
    private static object[] BuildGoogleResults(string query)
    {
        if (query.Contains("北京", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new
                {
                    rank = 1,
                    title = "北京市行政區劃 - 維基百科",
                    url = "https://zh.wikipedia.org/wiki/%E5%8C%97%E4%BA%AC%E5%B8%82%E8%A1%8C%E6%94%BF%E5%8D%80%E5%8A%83",
                    snippet = "北京市現轄多個市轄區，這是行政區劃總覽頁。"
                },
                new
                {
                    rank = 2,
                    title = "北京市行政區劃資料",
                    url = "https://www.beijing.gov.cn/renwen/bjgk/xzqh/",
                    snippet = "北京市政府提供的行政區劃資訊。"
                }
            ];
        }
        return
        [
            new
            {
                rank = 1,
                title = "Taipei Weather",
                url = "https://example.com/weather",
                snippet = "Rain later this afternoon."
            }
        ];
    }
    private static object[] BuildWikipediaResults(string query)
    {
        if (query.Contains("北京", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new
                {
                    rank = 1,
                    title = "北京市行政區劃",
                    url = "https://zh.wikipedia.org/wiki/%E5%8C%97%E4%BA%AC%E5%B8%82%E8%A1%8C%E6%94%BF%E5%8D%80%E5%8A%83",
                    snippet = "北京市現轄東城區、西城區、朝陽區、海淀區、豐台區、石景山區等多個市轄區。"
                },
                new
                {
                    rank = 2,
                    title = "北京市",
                    url = "https://zh.wikipedia.org/wiki/%E5%8C%97%E4%BA%AC%E5%B8%82",
                    snippet = "北京市是中華人民共和國首都，為直轄市。"
                }
            ];
        }
        return
        [
            new
            {
                rank = 1,
                title = "Wikipedia Test Result",
                url = "https://en.wikipedia.org/wiki/Test",
                snippet = "Synthetic wikipedia result for verify."
            }
        ];
    }
}

file sealed class FakeLegalEmbeddingService : EmbeddingService
{
    public FakeLegalEmbeddingService()
        : base(new EmbeddingOptions
        {
            Enabled = true,
            Provider = "fake",
            BaseUrl = "http://localhost:11434",
            Model = "fake-legal-embedding",
            Dimension = 3,
            TimeoutSeconds = 1
        })
    {
    }

    public override Task<float[]?> EmbedAsync(string text)
    {
        if (text.Contains("通訊交易", StringComparison.Ordinal) ||
            text.Contains("七日", StringComparison.Ordinal) ||
            text.Contains("解除契約", StringComparison.Ordinal) ||
            text.Contains("退貨", StringComparison.Ordinal))
        {
            return Task.FromResult<float[]?>(new[] { 1.0f, 0.0f, 0.0f });
        }

        if (text.Contains("消費者", StringComparison.Ordinal) ||
            text.Contains("交易", StringComparison.Ordinal))
        {
            return Task.FromResult<float[]?>(new[] { 0.8f, 0.2f, 0.0f });
        }

        if (text.Contains("咖啡", StringComparison.Ordinal) ||
            text.Contains("拉花", StringComparison.Ordinal))
        {
            return Task.FromResult<float[]?>(new[] { 0.0f, 1.0f, 0.0f });
        }

        return Task.FromResult<float[]?>(new[] { 0.0f, 0.0f, 1.0f });
    }
}

file sealed class LegalRagFtsRow
{
    [BaseOrm.Column("source_key")]
    public string? SourceKey { get; set; }

    [BaseOrm.Column("content")]
    public string? Content { get; set; }
}

file sealed class FakeBrowserPreviewHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var html = "<html><head><title>Example Preview</title></head><body><h1>Hello</h1><p>Preview body text.</p></body></html>";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html)
        });
    }
}

file sealed class FakeGoogleDriveHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != null &&
            request.RequestUri.Host.Contains("oauth2.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            var formBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (formBody.Contains("authorization_code", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"verify_oauth_access_token\",\"refresh_token\":\"verify_refresh_token\",\"scope\":\"https://www.googleapis.com/auth/drive.file\",\"token_type\":\"Bearer\",\"expires_in\":3600}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"verify_delegated_access_token\",\"token_type\":\"Bearer\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
            };
        }

        if (request.RequestUri != null &&
            request.RequestUri.Host.Contains("www.googleapis.com", StringComparison.OrdinalIgnoreCase) &&
            request.RequestUri.AbsoluteUri.Contains("/oauth2/v2/userinfo", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"email\":\"verify.user@example.com\"}", Encoding.UTF8, "application/json")
            };
        }

        if (request.RequestUri != null &&
            request.RequestUri.AbsoluteUri.Contains("/upload/drive/v3/files", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var body = Encoding.UTF8.GetString(bytes);
            var isMultipart = request.Content.Headers.ContentType?.MediaType?.Contains("multipart", StringComparison.OrdinalIgnoreCase) == true;
            if ((!isMultipart || bytes.Length == 0) &&
                !body.Contains("verify-reply", StringComparison.Ordinal) &&
                !body.Contains("drive delivery verify", StringComparison.Ordinal) &&
                !body.Contains("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase) &&
                !body.Contains("Generated Web Prototype", StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Expected multipart upload body to include generated artifact content.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"drive_file_verify\",\"name\":\"drive-delivery-test.txt\",\"mimeType\":\"text/plain\",\"webViewLink\":\"https://drive.google.com/file/d/drive_file_verify/view\",\"webContentLink\":\"https://drive.google.com/uc?id=drive_file_verify&export=download\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        if (request.RequestUri != null &&
            request.RequestUri.AbsoluteUri.Contains("/drive/v3/files/drive_file_verify/permissions", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"perm_verify\"}", Encoding.UTF8, "application/json")
            };
        }

        throw new Exception($"Unexpected Google Drive request: {request.Method} {request.RequestUri}");
    }
}

file static class VerifyUrlHelpers
{
    public static string ExtractQueryParam(string url, string name)
    {
        var uri = new Uri(url);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], name, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[1]);
        }

        throw new Exception($"Query parameter not found: {name}");
    }
}

file sealed class FakeWebHostEnvironment : IWebHostEnvironment
{
    public FakeWebHostEnvironment(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
        WebRootPath = contentRootPath;
        ApplicationName = "Broker.Verify";
        EnvironmentName = "Development";
    }

    public string ApplicationName { get; set; }
    public IFileProvider WebRootFileProvider { get; set; } = null!;
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}

file sealed class FakeAzureIisDeploymentSecretResolver : IAzureIisDeploymentSecretResolver
{
    public AzureIisDeploymentSecret? Resolve(string secretRef)
        => new()
        {
            UserName = "vm-user",
            Password = "vm-password"
        };
}

file sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRunSpec> Invocations { get; } = new();

    public Task<ProcessRunResult> RunAsync(ProcessRunSpec spec, CancellationToken cancellationToken = default)
    {
        Invocations.Add(spec);
        return Task.FromResult(new ProcessRunResult
        {
            ExitCode = 0,
            StandardOutput = $"ok:{spec.FileName}",
            StandardError = string.Empty
        });
    }
}

file sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
