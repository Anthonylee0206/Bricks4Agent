using System.Text.Json;
using Broker.NyaChat.Abstractions;
using Broker.Services;
using Broker.Services.NyaTools;
using BrokerCore.Data;
using BrokerCore.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

public static class NyaTierAuthTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Tier Authorization Tests ===");
        Console.WriteLine();

        WithDb((db, auth) =>
        {
            Check("no-permission-tool-allowed-without-profile", true, auth, "u_none", null);
            Check("no-profile-denies-query", false, auth, "u_none", "query");

            Seed(db, "line", "u_basic", Profile(HighLevelAccessTier.Basic, new HighLevelUserPermissions { AllowProduction = true }));
            Check("basic-query", true, auth, "u_basic", "query");
            Check("basic-transport", true, auth, "u_basic", "transport");

            Seed(db, "line", "u_member", Profile(HighLevelAccessTier.Member, new HighLevelUserPermissions { AllowProduction = true }));
            Check("member-quant-unmapped", false, auth, "u_member", "quant.read");

            Seed(db, "line", "u_noquery", Profile(HighLevelAccessTier.Basic, new HighLevelUserPermissions { AllowQuery = false }));
            Check("query-revoked", false, auth, "u_noquery", "query");
            Check("transport-still-on", true, auth, "u_noquery", "transport");

            var rejected = Profile(HighLevelAccessTier.Member, new HighLevelUserPermissions());
            rejected.RegistrationStatus = HighLevelRegistrationStatus.Rejected;
            Seed(db, "line", "u_rejected", rejected);
            Check("rejected-denied", false, auth, "u_rejected", "query");

            Seed(db, "line", "u_forged", Profile(HighLevelAccessTier.Basic, new HighLevelUserPermissions()), author: "prn_user");
            Check("untrusted-author-ignored", false, auth, "u_forged", "query");

            Seed(db, "line", "u_latest", Profile(HighLevelAccessTier.Basic, new HighLevelUserPermissions()), version: 1);
            Seed(db, "line", "u_latest", Profile(HighLevelAccessTier.Basic, new HighLevelUserPermissions { AllowQuery = false }), version: 2);
            Check("latest-version-wins", false, auth, "u_latest", "query");

            Seed(db, "discord", "123", Profile(HighLevelAccessTier.Basic, new HighLevelUserPermissions()));
            AssertEqual("non-line-prefix-stripped", true, auth.CanUse("discord:123", "discord", Schema("query")));
        });

        Console.WriteLine();
        Console.WriteLine($"=== Tier Authorization Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static HighLevelUserProfile Profile(string tier, HighLevelUserPermissions permissions)
        => new() { AccessTier = tier, Permissions = permissions };

    private static void Seed(BrokerDb db, string channel, string userId, HighLevelUserProfile profile,
        string author = "system:high-level", int version = 1)
        => db.Insert(new SharedContextEntry
        {
            EntryId = $"ctx_{Guid.NewGuid():N}",
            DocumentId = $"hlm.profile.{channel}.{userId}",
            Version = version,
            Key = "profile",
            ContentRef = JsonSerializer.Serialize(profile),
            AuthorPrincipalId = author,
            TaskId = SystemContextDocuments.GlobalTaskId
        });

    private static NyaToolSchema Schema(string? permission)
        => new() { Name = "t", DefaultEnabled = true, RequiredPermission = permission };

    private static void Check(string name, bool expected, BrokerTierToolAuthorizer auth, string userId, string? permission)
        => AssertEqual(name, expected, auth.CanUse(userId, "line", Schema(permission)));

    private static void WithDb(Action<BrokerDb, BrokerTierToolAuthorizer> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_tierauth_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            db.EnsureTable<SharedContextEntry>();
            body(db, new BrokerTierToolAuthorizer(db, NullLogger<BrokerTierToolAuthorizer>.Instance));
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private static void AssertEqual<T>(string name, T expected, T actual)
    {
        if (Equals(expected, actual)) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected '{expected}', got '{actual}'"); _failed++; }
    }
}
