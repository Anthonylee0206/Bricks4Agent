using System.Text.Encodings.Web;
using System.Text.Json;
using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;

namespace Broker.Services.Observability;

/// <summary>
/// 把報告存成 shared_context_entries 文件（專案慣例：狀態以 SharedContextEntry 文件保存，不另開資料表）。
/// 所有報告是同一份文件的不同版本，每份報告一個版本；讀取權限只開給管理員角色。
/// </summary>
public sealed class IncidentReportStore
{
    public const string DocumentId = "obs.incident.reports";
    private const string KeyPrefix = "obs.incident.";
    private const string AuthorPrincipalId = "system:incident-report";

    // shared context 的 ACL 以角色 ID 表示；報告只有管理員讀得到
    private const string Acl = "{\"read\":[\"role_admin\"],\"write\":[\"system:incident-report\"]}";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly BrokerDb _db;
    private readonly ILogger<IncidentReportStore> _logger;
    private readonly object _writeLock = new();

    public IncidentReportStore(BrokerDb db, ILogger<IncidentReportStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Save(IncidentReport report)
    {
        lock (_writeLock)
        {
            var latest = _db.Query<SharedContextEntry>(
                "SELECT * FROM shared_context_entries WHERE document_id = @docId ORDER BY version DESC LIMIT 1",
                new { docId = DocumentId }).FirstOrDefault();

            _db.Insert(new SharedContextEntry
            {
                EntryId = IdGen.New("ctx"),
                DocumentId = DocumentId,
                Version = (latest?.Version ?? 0) + 1,
                ParentVersion = latest?.Version,
                Key = KeyPrefix + report.ReportId,
                ContentRef = JsonSerializer.Serialize(report, Json),
                ContentType = "application/json",
                Acl = Acl,
                AuthorPrincipalId = AuthorPrincipalId,
                TaskId = "global",
                Tags = "[\"observability\",\"incident-report\"]",
                CreatedAt = report.CreatedAt,
            });
        }
    }

    /// <summary>最新的幾份報告，新的在前。</summary>
    public IReadOnlyList<IncidentReport> GetRecent(int limit)
    {
        var entries = _db.Query<SharedContextEntry>(
            "SELECT * FROM shared_context_entries WHERE document_id = @docId ORDER BY version DESC LIMIT @limit",
            new { docId = DocumentId, limit = Math.Clamp(limit, 1, 500) });
        return entries.Select(Deserialize).OfType<IncidentReport>().ToList();
    }

    public IncidentReport? Get(string reportId)
    {
        var entry = _db.Query<SharedContextEntry>(
            "SELECT * FROM shared_context_entries WHERE document_id = @docId AND key = @key LIMIT 1",
            new { docId = DocumentId, key = KeyPrefix + reportId }).FirstOrDefault();
        return entry is null ? null : Deserialize(entry);
    }

    private IncidentReport? Deserialize(SharedContextEntry entry)
    {
        try
        {
            return JsonSerializer.Deserialize<IncidentReport>(entry.ContentRef, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Incident report entry {EntryId} could not be read", entry.EntryId);
            return null;
        }
    }
}
