using BrokerCore.Data;
using BrokerCore.Services;
using FluentAssertions;
using Unit.Tests.Helpers;

namespace Unit.Tests.Core;

/// <summary>
/// 稽核雜湊鏈驗證：寫入 SQLite 再讀回來之後，正常的鏈要驗得過，被竄改的鏈要驗不過。
/// 原本 VerifyTraceIntegrity 以讀回的時間（Kind=Unspecified）格式化，少了寫入時的 "Z"，
/// 任何正常的鏈都驗證失敗；這組測試鎖住修正。
/// </summary>
public sealed class AuditServiceIntegrityTests : IDisposable
{
    private readonly BrokerDb _db = TestDb.CreateInMemory();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ChainWrittenToSqlite_VerifiesAfterReadingItBack()
    {
        var audit = new AuditService(_db);
        for (var i = 0; i < 3; i++)
            audit.RecordEvent("trace_integrity", $"EVENT_{i}", details: $"{{\"n\":{i}}}");

        audit.VerifyTraceIntegrity("trace_integrity").Should().BeTrue();
    }

    [Fact]
    public void TamperedEvent_FailsVerification()
    {
        var audit = new AuditService(_db);
        for (var i = 0; i < 3; i++)
            audit.RecordEvent("trace_tampered", $"EVENT_{i}", details: $"{{\"n\":{i}}}");

        _db.Execute(
            "UPDATE audit_events SET payload_digest = @digest WHERE trace_id = @trace AND trace_seq = 1",
            new { digest = new string('0', 64), trace = "trace_tampered" });

        audit.VerifyTraceIntegrity("trace_tampered").Should().BeFalse();
    }
}
