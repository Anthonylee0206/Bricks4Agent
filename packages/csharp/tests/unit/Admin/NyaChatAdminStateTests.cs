using System.Text.Json;
using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Admin;

public sealed class NyaChatAdminStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nya_admin_{Guid.NewGuid():N}");
    private readonly BrokerDb _db;
    private readonly ConfigStore _config;
    private readonly NyaMemoryService _memory;
    private readonly NyaAuditLogger _audit;

    public NyaChatAdminStateTests()
    {
        Directory.CreateDirectory(_directory);
        _db = BrokerDb.UseSqlite($"Data Source={Path.Combine(_directory, "test.db")};Pooling=False");
        _config = new ConfigStore(new NyaChatConfig
        {
            SoulsDirectory = Path.Combine(_directory, "souls"),
            DefaultSoulId = "custom_default",
            AuditEnabled = true
        });
        Directory.CreateDirectory(_config.Current.SoulsDirectory);
        _memory = new NyaMemoryService(_db, _config, NullLogger<NyaMemoryService>.Instance);
        _audit = new NyaAuditLogger(_db, _config, NullLogger<NyaAuditLogger>.Instance);
    }

    [Fact]
    public void Souls_ResolveBindingAndConfiguredDefaultAndBuiltinFallbackSeparately()
    {
        using var souls = new NyaSoulProvider(_config, _memory, NullLogger<NyaSoulProvider>.Instance);
        souls.SaveSoul(new SoulDefinition { SoulId = "custom_default", DisplayName = "Default" });
        souls.SaveSoul(new SoulDefinition { SoulId = "bound", DisplayName = "Bound" });

        var unbound = souls.ResolveSoul("user");
        unbound.BoundSoulId.Should().BeNull();
        unbound.Soul.SoulId.Should().Be("custom_default");
        unbound.Reason.Should().Be("configured_default");

        _memory.SetSoulBinding("user", "BOUND");
        souls.ResolveSoul("user").Reason.Should().Be("bound");
        souls.ResolveSoul("user").Soul.SoulId.Should().Be("bound");
        souls.DeleteSoul("bound").Should().BeTrue();
        var missing = souls.ResolveSoul("user");
        missing.BoundSoulId.Should().Be("BOUND");
        missing.Soul.SoulId.Should().Be("custom_default");
        missing.Reason.Should().Be("bound_soul_missing");

        _memory.RemoveSoulBinding("user").Should().BeTrue();
        _memory.RemoveSoulBinding("user").Should().BeFalse();
        souls.ResolveSoul("user").BoundSoulId.Should().BeNull();
        souls.ResolveSoul("user").Reason.Should().Be("configured_default");
        souls.DeleteSoul("custom_default").Should().BeTrue();
        souls.ResolveSoul("user").Soul.SoulId.Should().Be("fallback");
        souls.ResolveSoul("user").Reason.Should().Be("configured_default_missing");
        _memory.SetSoulBinding("user", "missing");
        souls.ResolveSoul("user").Reason.Should().Be("bound_and_default_missing");
    }

    [Fact]
    public void SoulReload_ReportsEachInvalidFileAndKeepsValidDefinitionsAvailable()
    {
        File.WriteAllText(Path.Combine(_config.Current.SoulsDirectory, "valid.json"),
            "{\"soul_id\":\"custom_default\",\"display_name\":\"Default\"}");
        File.WriteAllText(Path.Combine(_config.Current.SoulsDirectory, "broken.json"), "{");
        File.WriteAllText(Path.Combine(_config.Current.SoulsDirectory, "empty.json"), "{}");
        using var souls = new NyaSoulProvider(_config, _memory, NullLogger<NyaSoulProvider>.Instance);

        var failures = souls.ReloadSouls();

        failures.Select(f => f.File).Should().BeEquivalentTo("broken.json", "empty.json");
        failures.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Message));
        souls.ListSouls().Should().ContainSingle(s => s.Id == "custom_default");
    }

    [Fact]
    public void ToolToggle_FailedPersistenceRestoresPreviousStateAndPropagatesError()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["NyaChat:DataDirectory"] = _directory }).Build();
        var store = new NyaToolEnableStore(configuration, NullLogger<NyaToolEnableStore>.Instance);
        store.Set("search", true);
        var path = Path.Combine(_directory, "nya_tools.json");
        File.Delete(path);
        Directory.CreateDirectory(path);

        Action disableExisting = () => store.Set("search", false);
        disableExisting.Should().Throw<Exception>();
        store.Effective("search", false).Should().BeTrue();
        Action disableNew = () => store.Set("new_tool", false);
        disableNew.Should().Throw<Exception>();
        store.Effective("new_tool", true).Should().BeTrue();

        Directory.Delete(path);
        store.Set("search", false);
        var reloaded = new NyaToolEnableStore(configuration, NullLogger<NyaToolEnableStore>.Instance);
        reloaded.Effective("search", true).Should().BeFalse();
    }

    [Fact]
    public async Task Audit_AdminIdentityIsScopedAndSoulBindingIsVisibleForTargetUser()
    {
        using (_audit.BeginAdminOperation("operator", "permission_admin"))
        {
            await Task.Yield();
            _audit.LogSoulChange("soul_bind", "bound", "target");
            using (_audit.BeginAdminOperation("nested", "super_admin"))
                _audit.LogReload("prompt_reload", 2);
            _audit.LogSoulChange("soul_unbind", "bound", "target");
            _audit.LogReload("soul_reload", 1, new[] { new { file = "bad.json", message = "Invalid JSON" } }, "error");
        }
        _audit.LogMemoryTool("target", "note_save", "{}", "completed");

        var logs = _audit.GetLogs("target").Logs;
        var bound = logs.Single(l => l.Action == "soul_bind");
        using var detail = JsonDocument.Parse(bound.DetailJson);
        detail.RootElement.GetProperty("target_user_id").GetString().Should().Be("target");
        detail.RootElement.GetProperty("operator").GetProperty("username").GetString().Should().Be("operator");
        detail.RootElement.GetProperty("operator").GetProperty("role").GetString().Should().Be("permission_admin");
        logs.Single(l => l.Action == "soul_unbind").DetailJson.Should().Contain("operator");
        logs.Single(l => l.Action == "note_save").DetailJson.Should().NotContain("operator");
        _audit.GetLogs(action: "prompt_reload").Logs.Single().DetailJson.Should().Contain("nested");
        _audit.GetLogs(action: "soul_reload", result: "error").Logs.Should().ContainSingle();
        _audit.WriteFailureCount.Should().Be(0);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, true);
    }

    private sealed class ConfigStore(NyaChatConfig config) : INyaConfigStore<NyaChatConfig>
    {
        public NyaChatConfig Current => config;
        public Task UpdateAsync(NyaChatConfig updated, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
