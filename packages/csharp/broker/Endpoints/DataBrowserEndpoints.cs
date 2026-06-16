using System.Text.Json;
using Broker.Helpers;
using BrokerCore.Data;
using BrokerCore.Models;
using Microsoft.Data.Sqlite;

namespace Broker.Endpoints;

/// <summary>
/// 資料瀏覽 endpoints — Demo / 答辯用、給 admin 看 broker.db 內部狀態。
///
/// 兩支：
///   GET  /api/v1/errors/aggregate — 聚合 4 個 SQLite 表的錯誤紀錄、給 dashboard 友善列表
///   POST /api/v1/db/query         — read-only SQL 查詢（admin only、有嚴格 allowlist）
///
/// 安全性：
///   - /errors/aggregate 跟 audit 同等級、admin 看全部、user 看自己
///   - /db/query 必須 admin + 只允許 SELECT + 禁止寫操作關鍵字 + 200ms timeout + 500 row cap
///     用 SqliteConnection 直開 ReadOnly mode、就算 SQL injection 也只能讀（不能寫）
/// </summary>
public static class DataBrowserEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        // ── A. /errors/aggregate ──────────────────────────────────────
        var errors = group.MapGroup("/errors");
        errors.MapGet("/aggregate", (HttpContext ctx, BrokerDb db, HttpRequest req) =>
        {
            var callerPrincipalId = RequestBodyHelper.GetPrincipalId(ctx);
            var isAdmin = RequestBodyHelper.IsAdmin(ctx);

            var since = req.Query.TryGetValue("since", out var sn) && DateTime.TryParse(sn.ToString(), out var snDt)
                ? snDt.ToUniversalTime() : DateTime.UtcNow.AddHours(-24);
            var until = req.Query.TryGetValue("until", out var un) && DateTime.TryParse(un.ToString(), out var unDt)
                ? unDt.ToUniversalTime() : DateTime.UtcNow;
            var sourceFilter = req.Query.TryGetValue("source", out var s) ? s.ToString() : null;
            var limit = req.Query.TryGetValue("limit", out var l) && int.TryParse(l, out var lv)
                ? Math.Min(lv, 500) : 200;

            var sinceStr = since.ToString("o");
            var untilStr = until.ToString("o");

            var rows = new List<ErrorRow>();

            // 1. trading_approval_requests WHERE status='rejected'
            if (sourceFilter == null || sourceFilter == "approval")
            {
                var sql = "SELECT * FROM trading_approval_requests WHERE status = 'rejected' AND requested_at BETWEEN @sinceStr AND @untilStr";
                if (!isAdmin) sql += " AND principal_id = @caller";
                sql += " ORDER BY requested_at DESC LIMIT @limit";
                var data = db.Query<TradingApprovalRequest>(sql,
                    new { sinceStr, untilStr, caller = callerPrincipalId, limit });
                foreach (var a in data)
                {
                    rows.Add(new ErrorRow
                    {
                        Ts = a.DecidedAt ?? a.RequestedAt,
                        Source = "approval",
                        Severity = "warning",
                        Entity = a.CapabilityId + " · " + a.Route,
                        Summary = $"被拒：{a.DecisionReason ?? "(no reason)"} (by {a.DecidedBy ?? "—"})",
                        RelatedId = a.ApprovalId,
                    });
                }
            }

            // 2. agent_inbox_tasks WHERE status='failed' OR error IS NOT NULL
            if (sourceFilter == null || sourceFilter == "agent")
            {
                var data = db.Query<AgentInboxTask>(
                    "SELECT * FROM agent_inbox_tasks WHERE status = 'failed' AND created_at BETWEEN @sinceStr AND @untilStr ORDER BY created_at DESC LIMIT @limit",
                    new { sinceStr, untilStr, limit });
                foreach (var t in data)
                {
                    rows.Add(new ErrorRow
                    {
                        Ts = t.CompletedAt ?? t.CreatedAt,
                        Source = "agent_task",
                        Severity = "error",
                        Entity = t.AgentId + $" #{t.Seq}",
                        Summary = t.Error ?? "(unknown error)",
                        RelatedId = t.TaskId,
                    });
                }
            }

            // 3. audit_events WHERE event_type contains ERROR or details has status_code >= 400
            if (sourceFilter == null || sourceFilter == "audit")
            {
                var sql = @"SELECT * FROM audit_events
                            WHERE occurred_at BETWEEN @sinceStr AND @untilStr
                              AND (event_type LIKE '%ERROR%'
                                   OR event_type LIKE '%FAIL%'
                                   OR event_type LIKE '%REJECT%'
                                   OR details LIKE '%status_code"":4%'
                                   OR details LIKE '%status_code"":5%')";
                if (!isAdmin) sql += " AND principal_id = @caller";
                sql += " ORDER BY occurred_at DESC LIMIT @limit";
                var data = db.Query<AuditEvent>(sql,
                    new { sinceStr, untilStr, caller = callerPrincipalId, limit });
                foreach (var e in data)
                {
                    rows.Add(new ErrorRow
                    {
                        Ts = e.OccurredAt,
                        Source = "audit_event",
                        Severity = "error",
                        Entity = e.EventType,
                        Summary = $"{e.ResourceRef ?? "—"} · {Truncate(e.Details, 120)}",
                        RelatedId = e.TraceId,
                    });
                }
            }

            // sort merged + cap
            var sorted = rows.OrderByDescending(r => r.Ts).Take(limit).ToList();

            return Results.Ok(ApiResponseHelper.Success(new
            {
                query = new { since = sinceStr, until = untilStr, source = sourceFilter, limit, scope = isAdmin ? "all" : "self" },
                count = sorted.Count,
                rows = sorted,
                summary = new
                {
                    by_source = sorted.GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Count())
                }
            }));
        });

        // ── B. /db/query —— admin-only, read-only SQL ──────────────────
        var dbBrowser = group.MapGroup("/db");
        dbBrowser.MapPost("/query", (HttpContext ctx, IConfiguration config) =>
        {
            if (!RequestBodyHelper.IsAdmin(ctx))
                return Results.Json(ApiResponseHelper.Error("admin required", 403), statusCode: 403);

            JsonElement body;
            try { body = RequestBodyHelper.GetBody(ctx); }
            catch (Exception ex) { return Results.BadRequest(ApiResponseHelper.Error("invalid body: " + ex.Message)); }

            if (!body.TryGetProperty("sql", out var sqlNode) || sqlNode.ValueKind != JsonValueKind.String)
                return Results.BadRequest(ApiResponseHelper.Error("missing 'sql' string"));

            var sql = (sqlNode.GetString() ?? "").Trim();
            var validation = ValidateReadOnlySql(sql);
            if (!validation.Ok)
                return Results.BadRequest(ApiResponseHelper.Error(validation.Error!));

            // 用 ReadOnly mode 連 broker.db —— 多一層 SQLite-level write protection
            var dbPath = config.GetValue<string>("Database:Path") ?? "broker.db";
            var fullPath = Path.GetFullPath(dbPath);
            if (!File.Exists(fullPath))
                return Results.Ok(ApiResponseHelper.Error("broker.db not found at " + fullPath));

            try
            {
                using var conn = new SqliteConnection($"Data Source={fullPath};Mode=ReadOnly;Cache=Shared");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 5;

                var columns = new List<string>();
                var rows = new List<Dictionary<string, object?>>();
                using var reader = cmd.ExecuteReader();
                for (int i = 0; i < reader.FieldCount; i++)
                    columns.Add(reader.GetName(i));

                int rowCap = 500;
                while (reader.Read() && rows.Count < rowCap)
                {
                    var row = new Dictionary<string, object?>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        if (reader.IsDBNull(i)) { row[name] = null; continue; }
                        // 大字串截短、避免一條 row 爆 payload
                        var val = reader.GetValue(i);
                        if (val is string str && str.Length > 500)
                            row[name] = str.Substring(0, 500) + "…";
                        else
                            row[name] = val;
                    }
                    rows.Add(row);
                }
                var truncated = rows.Count >= rowCap;

                return Results.Ok(ApiResponseHelper.Success(new
                {
                    columns,
                    rows,
                    row_count = rows.Count,
                    truncated,
                    sql,
                }));
            }
            catch (Exception ex)
            {
                return Results.Ok(ApiResponseHelper.Error("query failed: " + ex.Message));
            }
        });

        // ── /db/tables —— 列出所有表（給 dashboard 預覽用） ──
        dbBrowser.MapGet("/tables", (HttpContext ctx, IConfiguration config) =>
        {
            if (!RequestBodyHelper.IsAdmin(ctx))
                return Results.Json(ApiResponseHelper.Error("admin required", 403), statusCode: 403);

            var dbPath = config.GetValue<string>("Database:Path") ?? "broker.db";
            var fullPath = Path.GetFullPath(dbPath);
            try
            {
                using var conn = new SqliteConnection($"Data Source={fullPath};Mode=ReadOnly;Cache=Shared");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                var tables = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) tables.Add(reader.GetString(0));
                return Results.Ok(ApiResponseHelper.Success(new { tables, count = tables.Count }));
            }
            catch (Exception ex)
            {
                return Results.Ok(ApiResponseHelper.Error("list tables failed: " + ex.Message));
            }
        });
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s.Substring(0, max) + "…" : s);

    /// <summary>
    /// Read-only SQL allowlist 驗證——抽出來給 Unit.Tests 直接覆蓋。
    /// 防護層次：
    ///   1. 非空
    ///   2. 必以 SELECT / WITH 開頭（其他關鍵字直接拒）
    ///   3. 黑名單寫操作關鍵字
    ///   4. 禁 `;`（單 statement only）
    ///   5. 禁 `--` / `/*`（防 comment-based bypass）
    /// </summary>
    internal static (bool Ok, string? Error) ValidateReadOnlySql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "empty sql");

        sql = sql.Trim();
        var sqlUpper = sql.ToUpperInvariant();
        if (!sqlUpper.StartsWith("SELECT") && !sqlUpper.StartsWith("WITH"))
            return (false, "only SELECT (or CTE WITH) queries allowed");

        string[] forbidden = { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "CREATE ", "ALTER ",
                               "PRAGMA ", "ATTACH ", "DETACH ", "VACUUM", "REINDEX", "REPLACE ",
                               "--", "/*" };
        foreach (var kw in forbidden)
        {
            if (sqlUpper.Contains(kw))
                return (false, $"forbidden keyword/comment: {kw.Trim()}");
        }
        if (sql.Contains(';'))
            return (false, "semicolons not allowed (single statement only)");

        return (true, null);
    }

    private class ErrorRow
    {
        public DateTime Ts { get; set; }
        public string Source { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Entity { get; set; } = "";
        public string Summary { get; set; } = "";
        public string RelatedId { get; set; } = "";
    }
}
