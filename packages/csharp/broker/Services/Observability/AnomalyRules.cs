using System.Globalization;

namespace Broker.Services.Observability;

/// <summary>
/// 確定性規則：給定一個時間窗的資料與基線，判定有哪些異常。
///
/// 「有沒有異常」只由這裡決定，LLM 只負責把結果寫成敘述。
/// 這個類別只做計算：不讀資料庫、不呼叫 LLM，所以每條規則都能用單元測試逐一驗證。
/// 規則代號與門檻對應設計文件第 4 節。
/// </summary>
public static class AnomalyRules
{
    public static readonly IReadOnlyList<string> AllRules =
        ["R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R11", "R12"];

    private const int MaxEvidence = 5;

    public sealed class Evaluation
    {
        public List<IncidentFinding> Findings { get; } = new();
        public IncidentCoverage Coverage { get; } = new();
        public List<string> BlindSpots { get; } = new();

        /// <summary>本窗的計數，存進報告，供之後的排程報告當基線。</summary>
        public Dictionary<string, double> Metrics { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 基線：之前幾份排程報告裡，每個計數「平均每分鐘」多少。
    /// <see cref="HasHistory"/> 為 false 表示還沒有任何排程報告，此時只看絕對門檻。
    /// </summary>
    public sealed record Baseline(bool HasHistory, IReadOnlyDictionary<string, double> PerMinute)
    {
        public static readonly Baseline None = new(false, new Dictionary<string, double>());

        /// <summary>依基線推算這個時間窗「預期」會有多少；沒有歷史時回傳 null。</summary>
        public double? Expected(string key, double windowMinutes)
            => HasHistory ? (PerMinute.TryGetValue(key, out var perMinute) ? perMinute : 0) * windowMinutes : null;
    }

    public static Evaluation Evaluate(IncidentWindowData data, Baseline baseline, IncidentRuleThresholds t)
    {
        var e = new Evaluation();
        var minutes = Math.Max(1.0, (data.WindowEnd - data.WindowStart).TotalMinutes);

        WorkerHeartbeatLost(data, e);                 // R1
        ExecutionFailures(data, t, e);                // R2
        PolicyDenials(data, baseline, minutes, t, e); // R3
        StuckApprovals(data, t, e);                   // R4
        PlanFailures(data, e);                        // R5
        HttpRules(data, baseline, minutes, t, e);     // R6、R7、R8
        LlmFailures(data, t, e);                      // R9
        InteractionErrors(data, e);                   // R10
        HealthDegraded(data, t, e);                   // R11
        SnapshotsStale(data, t, e);                   // R12

        e.Coverage.Scanned["execution_requests"] = data.ExecutionsScanned;
        e.Coverage.Scanned["approval_requests_pending"] = data.PendingApprovals.Count;
        e.Coverage.Scanned["audit_events"] = data.AuditScanned;
        e.Coverage.Scanned["http_responses"] = data.HttpResponsesTotal;
        e.Coverage.Scanned["observation_heartbeat_lost"] = data.HeartbeatLost.Count;
        e.Coverage.Scanned["health_snapshots"] = data.Snapshots.Count;
        e.Coverage.Scanned["llm_calls"] = data.Llm?.Calls ?? 0;
        e.Coverage.Scanned["line_interactions"] = data.InteractionsScanned;
        foreach (var error in data.SourceErrors)
            e.Coverage.Notes.Add($"資料來源讀取失敗：{error}");

        e.BlindSpots.AddRange(StandardBlindSpots(data));
        return e;
    }

    /// <summary>每份報告都會附上的盲點：這些狀況目前偵測不到，寫出來避免「沒看到」被當成「沒異常」。</summary>
    public static IEnumerable<string> StandardBlindSpots(IncidentWindowData data)
    {
        yield return "worker 註冊被拒絕時，broker 端沒有留下任何紀錄（WorkerSession 只回覆給 worker），這份報告看不到。";
        yield return "worker 崩潰、連線直接中斷時不會產生觀測事件，只能從 worker 數量下降（R11）間接看出。";
        yield return "HTTP 狀態碼與 LLM 呼叫的統計存在記憶體，broker 重啟後會歸零。";
        yield return "稽核雜湊鏈的完整性沒有列入：main 上的驗證功能有已知問題（時間格式往返），修正前無法使用。";
        yield return "需要使用者操作才會出錯的功能（例如審批頁按鈕），沒有人操作時不會出現在報告裡。";
        if (!data.PoolEnabled)
            yield return "Function Pool 未啟用：worker 相關的規則（R1、R11、R12）不適用。";
    }

    // ── R1：worker 心跳中斷 ──
    private static void WorkerHeartbeatLost(IncidentWindowData d, Evaluation e)
    {
        if (!d.PoolEnabled)
        {
            e.Coverage.RulesNotApplicable["R1"] = "Function Pool 未啟用";
            return;
        }

        e.Coverage.RulesRun.Add("R1");
        e.Metrics["heartbeat_lost"] = d.HeartbeatLost.Count;
        var noWorkersLeft = d.Health is { WorkerCount: 0 };

        foreach (var group in d.HeartbeatLost.GroupBy(h => h.WorkerId, StringComparer.Ordinal))
        {
            var last = group.Max(h => h.ObservedAt);
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R1",
                Title = "worker 心跳中斷",
                Severity = noWorkersLeft ? IncidentSeverity.Critical : IncidentSeverity.Warning,
                Subject = group.Key,
                Value = group.Count(),
                Detail = $"worker {group.Key} 超過心跳期限沒有回應，被移出可用清單"
                    + $"（本時段 {group.Count()} 次，最後一次 {Utc(last)}）"
                    + (noWorkersLeft ? "；目前已經沒有任何 worker 在線" : ""),
                Evidence = [group.Key],
            });
        }
    }

    // ── R2：執行失敗增加 ──
    private static void ExecutionFailures(IncidentWindowData d, IncidentRuleThresholds t, Evaluation e)
    {
        e.Coverage.RulesRun.Add("R2");
        foreach (var s in d.Executions)
        {
            var executed = s.Succeeded + s.Failed;
            e.Metrics[$"exec_failed:{s.CapabilityId}"] = s.Failed;
            if (executed == 0 || s.Failed < t.ExecutionFailedMin)
                continue;

            var rate = (double)s.Failed / executed;
            if (rate < t.ExecutionFailedRate)
                continue;

            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R2",
                Title = "執行失敗增加",
                Severity = rate >= t.ExecutionFailedCriticalRate ? IncidentSeverity.Critical : IncidentSeverity.Warning,
                Subject = s.CapabilityId,
                Value = s.Failed,
                Detail = $"能力 {s.CapabilityId} 本時段執行 {executed} 筆，失敗 {s.Failed} 筆（失敗率 {Pct(rate)}）",
                Evidence = s.SampleRequestIds.Take(MaxEvidence).ToList(),
            });
        }
    }

    // ── R3：政策拒絕暴增 ──
    private static void PolicyDenials(IncidentWindowData d, Baseline b, double minutes, IncidentRuleThresholds t, Evaluation e)
    {
        e.Coverage.RulesRun.Add("R3");
        foreach (var s in d.Executions.Where(s => s.Denied > 0))
        {
            var key = $"denied:{s.CapabilityId}";
            e.Metrics[key] = s.Denied;
            var expected = b.Expected(key, minutes);
            if (s.Denied < t.DeniedMin || (expected is not null && s.Denied <= t.SpikeFactor * expected.Value))
                continue;

            var reasons = s.TopDenyReasons.Count > 0 ? $"；主要原因：{string.Join("、", s.TopDenyReasons)}" : "";
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R3",
                Title = "政策拒絕暴增",
                Subject = s.CapabilityId,
                Value = s.Denied,
                Baseline = expected,
                Detail = $"能力 {s.CapabilityId} 本時段被政策拒絕 {s.Denied} 筆{BaselineText(expected)}{reasons}",
                Evidence = s.SampleRequestIds.Take(MaxEvidence).ToList(),
            });
        }
    }

    // ── R4：審批卡住（看目前狀態，不受時間窗限制）──
    private static void StuckApprovals(IncidentWindowData d, IncidentRuleThresholds t, Evaluation e)
    {
        e.Coverage.RulesRun.Add("R4");
        var now = d.WindowEnd;
        var stuck = d.PendingApprovals
            .Where(a => (now - a.CreatedAt).TotalMinutes >= t.ApprovalPendingMinutes
                        || (a.ExpiresAt is { } expires && expires <= now))
            .OrderBy(a => a.CreatedAt)
            .ToList();
        e.Metrics["approvals_stuck"] = stuck.Count;
        if (stuck.Count == 0)
            return;

        var expired = stuck.Count(a => a.ExpiresAt is { } x && x <= now);
        var oldestMinutes = (now - stuck[0].CreatedAt).TotalMinutes;
        e.Findings.Add(new IncidentFinding
        {
            RuleId = "R4",
            Title = "審批卡住",
            Subject = string.Join("、", stuck.Select(a => a.CapabilityId).Distinct().Take(3)),
            Value = stuck.Count,
            Detail = $"{stuck.Count} 筆審批仍在等待，最久已等 {oldestMinutes.ToString("F0", CultureInfo.InvariantCulture)} 分鐘"
                + (expired > 0 ? $"，其中 {expired} 筆已過期卻仍是等待狀態" : ""),
            Evidence = stuck.Take(MaxEvidence).Select(a => a.ApprovalId).ToList(),
        });
    }

    // ── R5：計畫失敗或死結 ──
    private static void PlanFailures(IncidentWindowData d, Evaluation e)
    {
        e.Coverage.RulesRun.Add("R5");
        foreach (var group in d.PlanEvents.GroupBy(p => p.EventType, StringComparer.Ordinal))
        {
            var count = group.Sum(p => p.Count);
            e.Metrics[$"plan:{group.Key}"] = count;
            var (label, severity) = group.Key switch
            {
                "PLAN_DEADLOCKED" => ("計畫死結", IncidentSeverity.Critical),
                "PLAN_FAILED" => ("計畫失敗", IncidentSeverity.Warning),
                _ => ("計畫節點失敗", IncidentSeverity.Warning),
            };
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R5",
                Title = label,
                Severity = severity,
                Subject = group.Key,
                Value = count,
                Detail = $"本時段有 {count} 筆{label}（稽核事件 {group.Key}）",
                Evidence = group.Select(p => p.SampleTraceId).OfType<string>().Distinct().Take(MaxEvidence).ToList(),
            });
        }
    }

    // ── R6 伺服器錯誤、R7 某路徑 4xx 暴增、R8 驗證失敗暴增 ──
    private static void HttpRules(IncidentWindowData d, Baseline b, double minutes, IncidentRuleThresholds t, Evaluation e)
    {
        if (!d.HttpMetricsAvailable)
        {
            foreach (var rule in new[] { "R6", "R7", "R8" })
                e.Coverage.RulesNotApplicable[rule] = "HTTP 狀態碼統計未啟用";
            return;
        }

        e.Coverage.RulesRun.AddRange(["R6", "R7", "R8"]);
        if (d.HttpMetricsStartedAt is { } started && started > d.WindowStart)
            e.Coverage.Notes.Add($"HTTP 狀態碼統計在 {Utc(started)} 才開始（broker 重啟），本時段較早的部分沒有資料。");
        if (d.HttpDroppedErrors > 0)
            e.Coverage.Notes.Add($"HTTP 錯誤統計曾達到組數上限，broker 啟動以來有 {d.HttpDroppedErrors} 筆錯誤回應沒有記到；R6–R8 可能低估。");

        // R6：任何 5xx
        foreach (var group in d.HttpErrors.Where(h => h.StatusCode >= 500).GroupBy(h => h.Path, StringComparer.Ordinal))
        {
            var count = group.Sum(h => h.Count);
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R6",
                Title = "伺服器錯誤",
                Subject = group.Key,
                Value = count,
                Detail = $"{group.Key} 本時段回應伺服器錯誤 {count} 筆（{StatusBreakdown(group)}）",
            });
        }

        // R7：同一路徑的 4xx（401、403 歸 R8）
        foreach (var group in d.HttpErrors
                     .Where(h => h.StatusCode is >= 400 and < 500 and not 401 and not 403)
                     .GroupBy(h => h.Path, StringComparer.Ordinal))
        {
            var count = group.Sum(h => h.Count);
            var key = $"4xx:{group.Key}";
            e.Metrics[key] = count;
            var expected = b.Expected(key, minutes);
            if (count < t.ClientErrorMinPerPath || (expected is not null && count <= t.SpikeFactor * expected.Value))
                continue;

            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R7",
                Title = "某個路徑 4xx 暴增",
                Subject = group.Key,
                Value = count,
                Baseline = expected,
                Detail = $"{group.Key} 本時段回應 4xx 共 {count} 筆（{StatusBreakdown(group)}）{BaselineText(expected)}",
            });
        }

        // R8：401、403 總數
        var authFailures = d.HttpErrors.Where(h => h.StatusCode is 401 or 403).ToList();
        var total = authFailures.Sum(h => h.Count);
        e.Metrics["auth_failures"] = total;
        var expectedAuth = b.Expected("auth_failures", minutes);
        var spike = expectedAuth is not null && total >= t.AuthFailureSpikeFloor && total > t.SpikeFactor * expectedAuth.Value;
        if (total >= t.AuthFailureMin || spike)
        {
            var topPaths = authFailures
                .GroupBy(h => h.Path, StringComparer.Ordinal)
                .Select(g => (Path: g.Key, Count: g.Sum(h => h.Count)))
                .OrderByDescending(p => p.Count)
                .Take(3)
                .ToList();
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R8",
                Title = "驗證失敗暴增",
                Subject = topPaths.Count > 0 ? topPaths[0].Path : "",
                Value = total,
                Baseline = expectedAuth,
                Detail = $"本時段驗證失敗（401／403）共 {total} 筆{BaselineText(expectedAuth)}；"
                    + $"最多的路徑：{string.Join("、", topPaths.Select(p => $"{p.Path}（{p.Count}）"))}",
            });
        }
    }

    // ── R9：LLM 呼叫失敗 ──
    private static void LlmFailures(IncidentWindowData d, IncidentRuleThresholds t, Evaluation e)
    {
        if (d.Llm is null)
        {
            e.Coverage.RulesNotApplicable["R9"] = "LLM 呼叫統計不可用";
            return;
        }

        e.Coverage.RulesRun.Add("R9");
        e.Metrics["llm_calls"] = d.Llm.Calls;
        e.Metrics["llm_failures"] = d.Llm.Failures;
        if (d.Llm.MetricsStartedAt > d.WindowStart)
            e.Coverage.Notes.Add($"LLM 呼叫統計在 {Utc(d.Llm.MetricsStartedAt)} 才開始（broker 重啟），數字可能偏低。");
        if (d.Llm.BufferMayBeTruncated)
            e.Coverage.Notes.Add("LLM 呼叫統計只保留最近 200 筆，本時段較早的呼叫可能沒算到。");

        if (d.Llm.Calls == 0 || d.Llm.Failures < t.LlmFailureMin)
            return;

        var rate = (double)d.Llm.Failures / d.Llm.Calls;
        if (rate < t.LlmFailureRate)
            return;

        e.Findings.Add(new IncidentFinding
        {
            RuleId = "R9",
            Title = "LLM 呼叫失敗",
            Subject = "llm-proxy",
            Value = d.Llm.Failures,
            Detail = $"本時段 LLM 呼叫 {d.Llm.Calls} 次，失敗 {d.Llm.Failures} 次（失敗率 {Pct(rate)}）",
        });
    }

    // ── R10：LINE 對話出錯 ──
    private static void InteractionErrors(IncidentWindowData d, Evaluation e)
    {
        e.Coverage.RulesRun.Add("R10");
        e.Metrics["interaction_errors"] = d.InteractionErrors.Count;
        if (d.InteractionErrors.Count == 0)
            return;

        var latest = d.InteractionErrors.Max(i => i.OccurredAt);
        e.Findings.Add(new IncidentFinding
        {
            RuleId = "R10",
            Title = "對話互動出錯",
            Subject = string.Join("、", d.InteractionErrors.Select(i => i.Channel).Distinct().Take(3)),
            Value = d.InteractionErrors.Count,
            // 錯誤訊息可能夾帶使用者輸入，不放進報告；用互動 ID 到後台的對話紀錄查看
            Detail = $"本時段有 {d.InteractionErrors.Count} 筆對話互動以錯誤結束（最近一筆 {Utc(latest)}）。"
                + "錯誤訊息可能含使用者輸入，報告不列出內容，請用相關 ID 到管理後台的對話紀錄查看",
            Evidence = d.InteractionErrors
                .OrderByDescending(i => i.OccurredAt)
                .Take(MaxEvidence)
                .Select(i => i.InteractionId)
                .ToList(),
        });
    }

    // ── R11：健康分數下降（目前狀態 + 時間窗內的快照）──
    private static void HealthDegraded(IncidentWindowData d, IncidentRuleThresholds t, Evaluation e)
    {
        if (!d.PoolEnabled)
        {
            e.Coverage.RulesNotApplicable["R11"] = "Function Pool 未啟用";
            return;
        }

        if (d.Health is null)
        {
            e.Coverage.RulesNotApplicable["R11"] = "無法取得目前的健康分數";
            return;
        }

        e.Coverage.RulesRun.Add("R11");
        var now = d.Health;
        e.Metrics["health_score"] = now.Score;
        e.Metrics["worker_count"] = now.WorkerCount;

        if (now.Status == HealthScoreService.NoWorkersStatus)
        {
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R11",
                Title = "沒有任何 worker 在線",
                Severity = IncidentSeverity.Critical,
                Subject = "function-pool",
                Value = 0,
                Detail = "報告產生當下沒有任何 worker 在線，所有需要 worker 的能力都無法執行",
            });
        }
        else if (now.Status is "degraded" or "critical")
        {
            e.Findings.Add(new IncidentFinding
            {
                RuleId = "R11",
                Title = "健康狀態不佳",
                Severity = now.Status == "critical" ? IncidentSeverity.Critical : IncidentSeverity.Warning,
                Subject = "function-pool",
                Value = now.Score,
                Detail = $"報告產生當下的健康分數 {now.Score}（{now.Status}），共 {now.WorkerCount} 個 worker",
            });
        }

        // worker 崩潰不會留下觀測事件，只能從數量下降看出來
        if (d.Snapshots.Count > 0)
        {
            var peak = d.Snapshots.Max(s => s.WorkerCount);
            if (now.WorkerCount < peak && now.Status != HealthScoreService.NoWorkersStatus)
            {
                e.Findings.Add(new IncidentFinding
                {
                    RuleId = "R11",
                    Title = "worker 數量下降",
                    Subject = "function-pool",
                    Value = now.WorkerCount,
                    Baseline = peak,
                    Detail = $"worker 數量從本時段最高的 {peak} 個降到 {now.WorkerCount} 個",
                });
            }

            var first = d.Snapshots[0].Score;
            var lowest = Math.Min(d.Snapshots.Min(s => s.Score), now.Score);
            if (first - lowest >= t.HealthScoreDrop)
            {
                e.Findings.Add(new IncidentFinding
                {
                    RuleId = "R11",
                    Title = "健康分數下降",
                    Subject = "function-pool",
                    Value = lowest,
                    Baseline = first,
                    Detail = $"健康分數在本時段從 {first} 掉到 {lowest}",
                });
            }
        }
    }

    // ── R12：觀測資料中斷（沒看到資料不等於正常）──
    private static void SnapshotsStale(IncidentWindowData d, IncidentRuleThresholds t, Evaluation e)
    {
        if (!d.PoolEnabled || d.SnapshotInterval <= TimeSpan.Zero)
        {
            e.Coverage.RulesNotApplicable["R12"] = "健康快照未啟用";
            return;
        }

        var limit = d.SnapshotInterval * t.SnapshotStaleIntervals;
        // 快照服務啟動後先等 1 分鐘才寫第一筆；broker 剛啟動時還無法判斷
        if (d.WindowEnd - d.HostStartedAt < limit + TimeSpan.FromMinutes(1))
        {
            e.Coverage.RulesNotApplicable["R12"] = $"broker 啟動未滿 {(limit + TimeSpan.FromMinutes(1)).TotalMinutes:F0} 分鐘，還無法判斷";
            return;
        }

        e.Coverage.RulesRun.Add("R12");
        if (d.LatestSnapshotAt is { } latest && d.WindowEnd - latest <= limit)
            return;

        e.Findings.Add(new IncidentFinding
        {
            RuleId = "R12",
            Title = "觀測資料中斷",
            Subject = "health_score_snapshots",
            Value = d.LatestSnapshotAt is { } at ? (d.WindowEnd - at).TotalMinutes : -1,
            Detail = d.LatestSnapshotAt is { } last
                ? $"最新的健康快照停在 {Utc(last)}，已超過 {t.SnapshotStaleIntervals} 個快照間隔沒有更新"
                : "broker 已啟動一段時間，卻沒有任何健康快照",
        });
    }

    private static string StatusBreakdown(IEnumerable<HttpStatusCount> counts)
        => string.Join("、", counts
            .GroupBy(c => c.StatusCode)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}×{g.Sum(c => c.Count)}"));

    private static string BaselineText(double? expected)
        => expected is null ? "" : $"，基線約 {expected.Value.ToString("F1", CultureInfo.InvariantCulture)} 筆";

    private static string Pct(double rate) => (rate * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
