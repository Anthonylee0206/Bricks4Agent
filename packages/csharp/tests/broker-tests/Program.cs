using Broker.Services;
using BrokerCore.Contracts;
using BrokerCore.Data;

// Make HighLevelCoordinator.BuildArtifactReply accessible
using static Broker.Services.HighLevelCoordinator;

Console.WriteLine("=== Broker.Tests: Artifact Delivery UX ===");
Console.WriteLine();

var passed = 0;
var failed = 0;

// ---------- Test 1: Drive 成功時的通知格式 ----------
{
    var driveResult = new GoogleDriveShareResult
    {
        Success = true,
        WebViewLink = "https://drive.google.com/file/d/ABC123/view",
        DownloadLink = "https://drive.google.com/uc?export=download&id=ABC123"
    };

    var body = LineArtifactDeliveryService.BuildNotificationBody("report.md", "/path/report.md", driveResult);

    AssertContains("Test1-title", body, "\u6a94\u6848\u5df2\u5b8c\u6210\u4e26\u4e0a\u50b3\u5230 Google Drive\u3002");
    AssertContains("Test1-filename", body, "\u6a94\u540d\uff1areport.md");
    AssertContains("Test1-download-label", body, "\u4e0b\u8f09\u9023\u7d50\uff1a");
    AssertContains("Test1-download-link", body, "https://drive.google.com/uc?export=download&id=ABC123");
    AssertContains("Test1-preview-label", body, "\u9810\u89bd\u9023\u7d50\uff1a");
    AssertContains("Test1-preview-link", body, "https://drive.google.com/file/d/ABC123/view");
    AssertNotContains("Test1-no-local-path", body, "本機路徑");
    AssertNotContains("Test1-no-raw-path", body, "/path/report.md");

    Console.WriteLine($"  Test 1 body output:");
    Console.WriteLine("  ---");
    foreach (var line in body.Split(Environment.NewLine))
        Console.WriteLine($"  | {line}");
    Console.WriteLine("  ---");
    Console.WriteLine();
}

// ---------- Test 2: Drive 失敗時的通知格式 ----------
{
    var body = LineArtifactDeliveryService.BuildNotificationBody("data.csv", "/path/data.csv", null);

    AssertContains("Test2-title", body, "Artifact created, but a downloadable link is not available yet.");
    AssertContains("Test2-filename", body, "\u6a94\u540d\uff1adata.csv");
    AssertContains("Test2-fallback", body, "downloadable link is not available yet");
    AssertContains("Test2-admin-help", body, "without exposing internal paths");
    AssertNotContains("Test2-no-local-path", body, "本機路徑");
    AssertNotContains("Test2-no-drive-link", body, "drive.google.com");

    Console.WriteLine($"  Test 2 body output:");
    Console.WriteLine("  ---");
    foreach (var line in body.Split(Environment.NewLine))
        Console.WriteLine($"  | {line}");
    Console.WriteLine("  ---");
    Console.WriteLine();
}

// ---------- Test 3: Drive 成功但只有 WebViewLink（無 DownloadLink）----------
{
    var driveResult = new GoogleDriveShareResult
    {
        Success = true,
        WebViewLink = "https://drive.google.com/file/d/XYZ/view",
        DownloadLink = ""
    };

    var body = LineArtifactDeliveryService.BuildNotificationBody("notes.txt", "/p/notes.txt", driveResult);

    AssertContains("Test3-title", body, "\u6a94\u6848\u5df2\u5b8c\u6210\u4e26\u4e0a\u50b3\u5230 Google Drive\u3002");
    AssertContains("Test3-preview", body, "\u9810\u89bd\u9023\u7d50\uff1a");
    AssertNotContains("Test3-no-download-section", body, "\u4e0b\u8f09\u9023\u7d50\uff1a");

    Console.WriteLine($"  Test 3 body output:");
    Console.WriteLine("  ---");
    foreach (var line in body.Split(Environment.NewLine))
        Console.WriteLine($"  | {line}");
    Console.WriteLine("  ---");
    Console.WriteLine();
}

// ---------- Test 4: Drive Success=false ----------
{
    var driveResult = new GoogleDriveShareResult
    {
        Success = false,
        Message = "quota_exceeded"
    };

    var body = LineArtifactDeliveryService.BuildNotificationBody("big.json", "/p/big.json", driveResult);

    AssertContains("Test4-title", body, "Artifact created, but a downloadable link is not available yet.");
    AssertContains("Test4-fallback", body, "downloadable link is not available yet");
    AssertNotContains("Test4-no-drive-link", body, "drive.google.com");
    AssertNotContains("Test4-no-local-path", body, "/p/big.json");

    Console.WriteLine($"  Test 4 body output:");
    Console.WriteLine("  ---");
    foreach (var line in body.Split(Environment.NewLine))
        Console.WriteLine($"  | {line}");
    Console.WriteLine("  ---");
    Console.WriteLine();
}

// ---------- Test 4b: Broker fallback download link ----------
{
    var link = "https://example-sidecar.ngrok-free.dev/api/v1/artifacts/download/artifact_test?exp=111&sig=abc";
    var body = LineArtifactDeliveryService.BuildNotificationBody(
        "local.zip",
        "C:\\internal\\local.zip",
        new GoogleDriveShareResult { Success = false, Message = "drive_not_configured" },
        link);

    AssertContains("Test4b-download-label", body, "\u4e0b\u8f09\u9023\u7d50\uff1a");
    AssertContains("Test4b-download-link", body, link);
    AssertNotContains("Test4b-no-local-path", body, "C:\\internal\\local.zip");
    Console.WriteLine($"  Test 4b body output:");
    Console.WriteLine("  ---");
    foreach (var line in body.Split(Environment.NewLine))
        Console.WriteLine($"  | {line}");
    Console.WriteLine("  ---");
    Console.WriteLine();
}

// ---------- Test 4c: Signed broker download URL ----------
{
    var tempDir = Path.Combine(Path.GetTempPath(), "b4a-artifact-download-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);

    try
    {
        var dbPath = Path.Combine(tempDir, "broker.db");
        using var db = new BrokerDb($"Data Source={dbPath};Pooling=False");
        new BrokerDbInitializer(db).Initialize();

        var artifactPath = Path.Combine(tempDir, "artifact.txt");
        await File.WriteAllTextAsync(artifactPath, "download-bytes", new System.Text.UTF8Encoding(false));

        var lastUrlPath = Path.Combine(tempDir, ".last-tunnel-url");
        await File.WriteAllTextAsync(lastUrlPath, "https://example-sidecar.ngrok-free.dev/webhook/line/", new System.Text.UTF8Encoding(false));

        var workspace = new HighLevelLineWorkspaceService(db, new HighLevelCoordinatorOptions
        {
            AccessRoot = tempDir
        });
        var recorded = workspace.RecordArtifact(new HighLevelLineArtifactRecord
        {
            UserId = "download-user",
            FileName = "artifact.txt",
            Format = "txt",
            FilePath = artifactPath,
            DocumentsRoot = tempDir,
            OverallStatus = "completed",
            Success = true
        });

        var options = new BrokerArtifactDownloadOptions
        {
            SigningSecret = "unit-test-download-secret",
            LinkTtlMinutes = 60,
            SidecarLastTunnelUrlPath = lastUrlPath
        };
        var service = new BrokerArtifactDownloadService(
            workspace,
            new SidecarPublicUrlResolver(options),
            options);

        var signed = service.CreateSignedDownloadUrl(recorded.ArtifactId, DateTimeOffset.UtcNow);
        AssertTrue("Test4c-signed-url-created", signed != null && signed.Contains("/api/v1/artifacts/download/", StringComparison.Ordinal));
        AssertTrue("Test4c-signed-url-base", signed != null && signed.StartsWith("https://example-sidecar.ngrok-free.dev/", StringComparison.Ordinal));

        var uri = new Uri(signed!);
        var query = ParseQuery(uri.Query);
        var exp = long.Parse(query["exp"]);
        var sig = query["sig"];

        var resolved = service.ValidateAndResolve(recorded.ArtifactId, exp, sig, DateTimeOffset.UtcNow);
        AssertTrue("Test4c-valid-resolves", resolved.IsValid);
        AssertEqual("Test4c-safe-file-name", resolved.SafeFileName, "artifact.txt");

        var invalid = service.ValidateAndResolve(recorded.ArtifactId, exp, "bad-signature", DateTimeOffset.UtcNow);
        AssertTrue("Test4c-invalid-rejected", !invalid.IsValid);

        var expired = service.ValidateAndResolve(recorded.ArtifactId, exp, sig, DateTimeOffset.FromUnixTimeSeconds(exp + 1));
        AssertTrue("Test4c-expired-rejected", expired.IsExpired);

        db.Dispose();
    }
    finally
    {
        DeleteDirectoryWithRetry(tempDir);
    }
}

// ---------- Test 5: BuildArtifactReply — Drive 成功 ----------
{
    Console.WriteLine("--- BuildArtifactReply Tests ---");
    var result = new HighLevelDocumentArtifactResult
    {
        Success = true,
        FileName = "report.md",
        Delivery = new LineArtifactDeliveryResult
        {
            Success = true,
            FilePath = "/path/report.md",
            GoogleDrive = new GoogleDriveShareResult
            {
                Success = true,
                WebViewLink = "https://drive.google.com/file/d/X/view",
                DownloadLink = "https://drive.google.com/uc?export=download&id=X"
            }
        }
    };

    var reply = HighLevelCoordinator.BuildArtifactReply(result);
    AssertContains("Test5-user-friendly", reply, "report.md");
    AssertContains("Test5-mentions-download", reply, "下載連結");
    AssertNotContains("Test5-no-artifact-path", reply, "artifact_path");
    AssertNotContains("Test5-no-artifact-file", reply, "artifact_file:");
    AssertNotContains("Test5-no-raw-path", reply, "/path/report.md");
    Console.WriteLine($"  Reply: {reply}");
    Console.WriteLine();
}

// ---------- Test 6: BuildArtifactReply — Drive 失敗 ----------
{
    var result = new HighLevelDocumentArtifactResult
    {
        Success = true,
        FileName = "data.csv",
        Delivery = new LineArtifactDeliveryResult
        {
            Success = true,
            FilePath = "/path/data.csv",
            GoogleDrive = new GoogleDriveShareResult { Success = false }
        }
    };

    var reply = HighLevelCoordinator.BuildArtifactReply(result);
    AssertContains("Test6-mentions-file", reply, "data.csv");
    AssertContains("Test6-mentions-upload-issue", reply, "雲端上傳未完成");
    AssertNotContains("Test6-no-technical-ids", reply, "artifact_delivery");
    Console.WriteLine($"  Reply: {reply}");
    Console.WriteLine();
}

// ---------- Test 7: BuildArtifactReply — 生成失敗 ----------
{
    var result = new HighLevelDocumentArtifactResult
    {
        Success = false,
        Message = "llm_timeout",
        FileName = "fail.md",
        Delivery = new LineArtifactDeliveryResult { Success = false, FilePath = "/p/f" }
    };

    var reply = HighLevelCoordinator.BuildArtifactReply(result);
    AssertContains("Test7-failure-message", reply, "文件生成失敗");
    AssertContains("Test7-reason", reply, "llm_timeout");
    AssertNotContains("Test7-no-artifact-path", reply, "artifact_path");
    Console.WriteLine($"  Reply: {reply}");
    Console.WriteLine();
}

// ---------- Test 8: BuildArtifactReply — no delivery ----------
{
    var result = new HighLevelDocumentArtifactResult
    {
        Success = true,
        FileName = "test.md",
        Delivery = null
    };

    var reply = HighLevelCoordinator.BuildArtifactReply(result);
    AssertContains("Test8-success-no-delivery", reply, "下載連結");
    Console.WriteLine($"  Reply: {reply}");
    Console.WriteLine();
}

// ---------- Test 9: Code generator fallback — tic-tac-toe ----------
{
    var draft = new HighLevelTaskDraft
    {
        TaskType = "code_gen",
        ProjectName = "game1",
        ProjectFolderName = "game1",
        Title = "game1",
        Summary = "建立 井字遊戲的網頁",
        OriginalMessage = "/建立 井字遊戲的網頁"
    };

    var method = typeof(HighLevelCodeArtifactService).GetMethod(
        "BuildFallbackHtml",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    AssertTrue("Test9-method-found", method != null);

    var html = (string)method!.Invoke(null, new object[] { draft })!;
    AssertContains("Test9-title", html, "井字遊戲");
    AssertContains("Test9-board", html, "data-cell");
    AssertContains("Test9-winning-lines", html, "winningLines");
    AssertContains("Test9-reset", html, "重新開始");
    AssertContains("Test9-custom-component-theme", html, "./runtime/ui_components/theme.css");
    AssertContains("Test9-custom-component-import", html, "import('./runtime/ui_components/index.js')");
    AssertContains("Test9-custom-component-basic-button", html, "BasicButton");
    AssertNotContains("Test9-no-generic-shell", html, "Generated Web Prototype");
    Console.WriteLine();
}

// ---------- Test 10: Code generator routes website replica requests ----------
{
    var draft = new HighLevelTaskDraft
    {
        TaskType = "code_gen",
        ProjectName = "ntub-remake",
        ProjectFolderName = "ntub-remake",
        Title = "ntub-remake",
        Summary = "參照 https://www.ntub.edu.tw/ 重製成可離線瀏覽的多頁網站，深度3，最多200頁",
        OriginalMessage = "/建立 參照 https://www.ntub.edu.tw/ 重製成可離線瀏覽的多頁網站：同網域 *.ntub.edu.tw 以廣度優先爬取，深度 3 層、最多 200 頁"
    };

    var detectMethod = typeof(HighLevelCodeArtifactService).GetMethod(
        "IsWebsiteReplicaRequest",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    AssertTrue("Test10-detect-method-found", detectMethod != null);
    AssertTrue("Test10-website-replica-detected", (bool)detectMethod!.Invoke(null, new object[] { draft })!);

    var urlMethod = typeof(HighLevelCodeArtifactService).GetMethod(
        "ExtractFirstHttpUrl",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    AssertTrue("Test10-url-method-found", urlMethod != null);
    var extractedUrl = (string?)urlMethod!.Invoke(null, new object[] { draft.OriginalMessage });
    AssertContains("Test10-url-extracted", extractedUrl ?? "", "https://www.ntub.edu.tw/");

    var optionsMethod = typeof(HighLevelCodeArtifactService).GetMethod(
        "ResolveStaticReplicaOptions",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    AssertTrue("Test10-options-method-found", optionsMethod != null);
    var options = optionsMethod!.Invoke(null, new object[] { draft, extractedUrl! })!;
    var maxDepth = (int)options.GetType().GetProperty("MaxDepth")!.GetValue(options)!;
    var maxPages = (int)options.GetType().GetProperty("MaxPages")!.GetValue(options)!;
    var domainSuffix = (string)options.GetType().GetProperty("DomainSuffix")!.GetValue(options)!;
    AssertTrue("Test10-depth", maxDepth == 3);
    AssertTrue("Test10-pages", maxPages == 200);
    AssertTrue("Test10-domain", domainSuffix == "ntub.edu.tw");
    Console.WriteLine();
}

// ---------- Summary (Unit Tests) ----------
Console.WriteLine($"=== Artifact Delivery Unit Results: {passed} passed, {failed} failed ===");
Console.WriteLine();

// ---------- Query Quality Tests ----------
var (qPassed, qFailed) = Broker.Tests.QueryTests.Run();
passed += qPassed;
failed += qFailed;

Console.WriteLine();

var (bdPassed, bdFailed) = Broker.Tests.BrowserAndDeployTests.Run();
passed += bdPassed;
failed += bdFailed;

Console.WriteLine();

var (nyaPassed, nyaFailed) = Broker.Tests.NyaLlmTests.Run();
passed += nyaPassed;
failed += nyaFailed;

Console.WriteLine();

var (tbPassed, tbFailed) = Broker.Tests.NyaTokenBudgetTests.Run();
passed += tbPassed;
failed += tbFailed;

Console.WriteLine();

var (ptPassed, ptFailed) = Broker.Tests.NyaPromptTests.Run();
passed += ptPassed;
failed += ptFailed;

Console.WriteLine();

var (ccPassed, ccFailed) = Broker.Tests.NyaConcurrencyTests.Run();
passed += ccPassed;
failed += ccFailed;

Console.WriteLine();

var (tePassed, teFailed) = Broker.Tests.NyaToolExecutionTests.Run();
passed += tePassed;
failed += teFailed;

Console.WriteLine();

var (uxPassed, uxFailed) = Broker.Tests.NyaChatUxTests.Run();
passed += uxPassed;
failed += uxFailed;

Console.WriteLine();

var (stPassed, stFailed) = Broker.Tests.NyaSearchToolTests.Run();
passed += stPassed;
failed += stFailed;

Console.WriteLine();

var (tpPassed, tpFailed) = Broker.Tests.NyaTopicTests.Run();
passed += tpPassed;
failed += tpFailed;

Console.WriteLine();

var (dsPassed, dsFailed) = Broker.Tests.NyaDbSeparationTests.Run();
passed += dsPassed;
failed += dsFailed;

Console.WriteLine();

var (tdPassed, tdFailed) = Broker.Tests.NyaTechDebtTests.Run();
passed += tdPassed;
failed += tdFailed;

Console.WriteLine();

var (lpPassed, lpFailed) = Broker.Tests.NyaLlmProfileStoreTests.Run();
passed += lpPassed;
failed += lpFailed;

Console.WriteLine();

var (pmPassed, pmFailed) = Broker.Tests.NyaProviderMappingTests.Run();
passed += pmPassed;
failed += pmFailed;

Console.WriteLine();

var (laPassed, laFailed) = Broker.Tests.NyaLlmAdminTests.Run();
passed += laPassed;
failed += laFailed;

Console.WriteLine();

var (dseedPassed, dseedFailed) = Broker.Tests.NyaDemoSeederTests.Run();
passed += dseedPassed;
failed += dseedFailed;

Console.WriteLine();

var (mqPassed, mqFailed) = Broker.Tests.NyaMemoryQualityTests.Run();
passed += mqPassed;
failed += mqFailed;

Console.WriteLine();

var (raPassed, raFailed) = Broker.Tests.NyaRoleAdminTests.Run();
passed += raPassed;
failed += raFailed;

Console.WriteLine();

var (ggPassed, ggFailed) = Broker.Tests.NyaConversationTopicTests.Run();
passed += ggPassed;
failed += ggFailed;

Console.WriteLine();

var (mv2Passed, mv2Failed) = Broker.Tests.NyaMemoryV2Tests.Run();
passed += mv2Passed;
failed += mv2Failed;

Console.WriteLine();

var (taPassed, taFailed) = Broker.Tests.NyaTierAuthTests.Run();
passed += taPassed;
failed += taFailed;

Console.WriteLine();

var (agentPassed, agentFailed) = Broker.Tests.AgentContainerTests.Run();
passed += agentPassed;
failed += agentFailed;

Console.WriteLine();

var (execAdapterPassed, execAdapterFailed) = Broker.Tests.ExecutionAdapterTests.Run();
passed += execAdapterPassed;
failed += execAdapterFailed;

Console.WriteLine();

var (approvalPassed, approvalFailed) = Broker.Tests.ApprovalLifecycleTests.Run();
passed += approvalPassed;
failed += approvalFailed;

Console.WriteLine();

var (linkPassed, linkFailed) = Broker.Tests.ApprovalLinkTests.Run();
passed += linkPassed;
failed += linkFailed;

Console.WriteLine();
Console.WriteLine($"=== Unit Test Results: {passed} passed, {failed} failed ===");
if (failed > 0)
{
    Console.Error.WriteLine($"FAILED: {failed} unit test assertion(s) did not pass.");
    Environment.Exit(1);
}
Console.WriteLine("UNIT TESTS ALL PASSED.");
Console.WriteLine();

// ---------- Integration Tests ----------
if (args.Length > 0 && args[0] == "--integration")
{
    var brokerUrl = args.Length > 1 ? args[1] : "http://localhost:5000";
    var (iPassed, iFailed) = await Broker.Tests.IntegrationTest.RunAsync(brokerUrl);
    passed += iPassed;
    failed += iFailed;

    Console.WriteLine();
    Console.WriteLine($"=== Total Results: {passed} passed, {failed} failed ===");
    if (failed > 0)
    {
        Console.Error.WriteLine($"FAILED: {failed} total assertion(s) did not pass.");
        Environment.Exit(1);
    }
    Console.WriteLine("ALL TESTS PASSED.");
}
else
{
    Console.WriteLine("Tip: Run with --integration [broker-url] for end-to-end tests.");
}

void AssertContains(string name, string actual, string expected)
{
    if (actual.Contains(expected))
    {
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    else
    {
        Console.Error.WriteLine($"  [FAIL] {name}: expected to contain \"{expected}\"");
        Console.Error.WriteLine($"         actual: \"{actual[..Math.Min(200, actual.Length)]}\"");
        failed++;
    }
}

void AssertNotContains(string name, string actual, string notExpected)
{
    if (!actual.Contains(notExpected))
    {
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    else
    {
        Console.Error.WriteLine($"  [FAIL] {name}: expected NOT to contain \"{notExpected}\"");
        failed++;
    }
}

void AssertTrue(string name, bool condition)
{
    if (condition)
    {
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    else
    {
        Console.Error.WriteLine($"  [FAIL] {name}");
        failed++;
    }
}

void AssertEqual(string name, string? actual, string expected)
{
    if (actual == expected)
    {
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    else
    {
        Console.Error.WriteLine($"  [FAIL] {name}: expected \"{expected}\", got \"{actual}\"");
        failed++;
    }
}

Dictionary<string, string> ParseQuery(string query)
{
    return query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(
            parts => Uri.UnescapeDataString(parts[0]),
            parts => Uri.UnescapeDataString(parts[1]));
}

void DeleteDirectoryWithRetry(string path)
{
    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return;
        }
        catch (IOException) when (attempt < 4)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(100);
        }
        catch (UnauthorizedAccessException) when (attempt < 4)
        {
            Thread.Sleep(100);
        }
    }
}
