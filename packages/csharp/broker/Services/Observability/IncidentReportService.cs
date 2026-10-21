using System.Diagnostics;
using System.Text.Json;
using BrokerCore;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Services.Observability;

/// <summary>
/// 產生異常偵測報告：收集（<see cref="AnomalyDigestBuilder"/>）→ 規則判定（<see cref="AnomalyRules"/>）
/// → 敘述（<see cref="IncidentNarrator"/>）→ 保存（<see cref="IncidentReportStore"/>）＋寫觀測事件。
///
/// 排程：<see cref="IncidentReportOptions.Enabled"/> 為 true 時每隔 IntervalMinutes 跑一次，
/// 時間窗等於間隔、首尾相接，不會有沒人看的空窗。管理員也可以手動觸發（<see cref="RunAsync"/>）。
/// 每個觸發的規則都寫一筆 ObservationEvent，會出現在 line-admin.html 既有的告警頁。
/// </summary>
public sealed class IncidentReportService : BackgroundService
{
    /// <summary>稽核事件的插入順序和時間可能有些微交錯，接續上一窗時往回多掃這麼多筆。</summary>
    private const long WatermarkSlack = 1000;

    private readonly IncidentReportOptions _options;
    private readonly AnomalyDigestBuilder _builder;
    private readonly IncidentNarrator _narrator;
    private readonly IncidentReportStore _store;
    private readonly IObservationService _observations;
    private readonly ILogger<IncidentReportService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IncidentReportService(
        IncidentReportOptions options,
        AnomalyDigestBuilder builder,
        IncidentNarrator narrator,
        IncidentReportStore store,
        IObservationService observations,
        ILogger<IncidentReportService> logger)
    {
        _options = options;
        _builder = builder;
        _narrator = narrator;
        _store = store;
        _observations = observations;
        _logger = logger;
    }

    /// <summary>broker 這次啟動的時間（這個 singleton 在 host 啟動時建立）。</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Incident report schedule is disabled (Observability:IncidentReport:Enabled=false); manual runs only");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_options.IntervalMinutes, 1, 24 * 60));
        _logger.LogInformation("Incident report schedule started, interval={Minutes}min", interval.TotalMinutes);

        var windowStart = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            var windowEnd = DateTime.UtcNow;
            try
            {
                await RunAsync(windowStart, windowEnd, IncidentReportTriggers.Scheduled, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Incident report run failed for window {Start:O}..{End:O}", windowStart, windowEnd);
            }

            // 下一窗從這一窗的結尾開始：首尾相接，不留空窗
            windowStart = windowEnd;
        }
    }

    /// <summary>產生 [start, end) 的報告並保存。同一時間只會有一份報告在產生。</summary>
    public async Task<IncidentReport> RunAsync(DateTime start, DateTime end, string trigger, CancellationToken ct = default)
    {
        start = AsUtc(start);
        end = AsUtc(end);
        if (end <= start)
            throw new ArgumentException("The report window must end after it starts.", nameof(end));

        await _gate.WaitAsync(ct);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var history = _store.GetRecent(Math.Max(_options.BaselineReports, 1) * 4);
            var floor = AuditFloor(history, start);
            var data = await _builder.BuildAsync(start, end, floor, StartedAt, ct);
            var evaluation = AnomalyRules.Evaluate(data, BuildBaseline(history, _options.BaselineReports), _options.Thresholds);

            var report = new IncidentReport
            {
                ReportId = IdGen.New("incident"),
                Trigger = trigger,
                WindowStart = start,
                WindowEnd = end,
                CreatedAt = DateTime.UtcNow,
                Findings = evaluation.Findings,
                Coverage = evaluation.Coverage,
                BlindSpots = evaluation.BlindSpots,
                Metrics = evaluation.Metrics,
                AuditWatermark = Math.Max(data.AuditMaxEventId, floor),
                MaxSeverity = evaluation.Findings.Any(f => f.Severity == IncidentSeverity.Critical)
                    ? IncidentSeverity.Critical
                    : evaluation.Findings.Count > 0 ? IncidentSeverity.Warning : "none",
            };

            if (_options.Narrate)
            {
                var narration = await _narrator.NarrateAsync(report, ct);
                report.Narrative = narration.Text;
                report.NarrativeStatus = narration.Status;
                report.NarrativeModel = narration.Model;
                report.NarrativeMissingRules = narration.MissingRules.ToList();
            }
            else
            {
                report.NarrativeStatus = "disabled";
            }

            report.DurationMs = stopwatch.ElapsedMilliseconds;
            _store.Save(report);
            RecordObservations(report);

            _logger.LogInformation(
                "Incident report {ReportId} ({Trigger}) {Start:O}..{End:O}: {Count} finding(s), max severity {Severity}, narrative {Narrative}, {Ms}ms",
                report.ReportId, trigger, start, end, report.Findings.Count, report.MaxSeverity,
                report.NarrativeStatus, report.DurationMs);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 基線：之前幾份「排程」報告裡，每個計數平均每分鐘多少。
    /// 手動報告的時間窗長短不一（常常只是臨時查一段），不拿來定義「平常」。
    /// </summary>
    public static AnomalyRules.Baseline BuildBaseline(IReadOnlyList<IncidentReport> history, int count)
    {
        var usable = history
            .Where(r => r.Trigger == IncidentReportTriggers.Scheduled)
            .Where(r => (r.WindowEnd - r.WindowStart).TotalMinutes >= 1)
            .Take(Math.Max(count, 1))
            .ToList();
        if (usable.Count == 0)
            return AnomalyRules.Baseline.None;

        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var report in usable)
        {
            var minutes = (report.WindowEnd - report.WindowStart).TotalMinutes;
            foreach (var (key, value) in report.Metrics)
                sums[key] = sums.GetValueOrDefault(key) + value / minutes;
        }

        // 某份報告沒有這個計數＝那一窗是 0，所以除以報告份數而不是出現次數
        return new AnomalyRules.Baseline(true, sums.ToDictionary(kv => kv.Key, kv => kv.Value / usable.Count, StringComparer.Ordinal));
    }

    /// <summary>接續上一份報告時，稽核事件只掃上一份水位線之後的部分；查較早的時段就從頭掃。</summary>
    private static long AuditFloor(IReadOnlyList<IncidentReport> history, DateTime start)
    {
        var latest = history.FirstOrDefault();
        if (latest is null || latest.AuditWatermark <= 0 || start < latest.WindowEnd - TimeSpan.FromMinutes(1))
            return 0;
        return Math.Max(0, latest.AuditWatermark - WatermarkSlack);
    }

    private void RecordObservations(IncidentReport report)
    {
        foreach (var finding in report.Findings)
        {
            try
            {
                _observations.Record(new ObservationEvent
                {
                    ObservationId = IdGen.New("obs"),
                    Source = ObservationSource.Internal,
                    EventType = $"INCIDENT_{finding.RuleId}",
                    TraceId = report.ReportId,
                    WorkerId = finding.RuleId == "R1" ? finding.Subject : null,
                    ObservedState = JsonSerializer.Serialize(new
                    {
                        rule_id = finding.RuleId,
                        subject = finding.Subject,
                        value = finding.Value,
                        baseline = finding.Baseline,
                    }),
                    Severity = finding.Severity == IncidentSeverity.Critical
                        ? ObservationSeverity.Critical
                        : ObservationSeverity.Warning,
                    Details = JsonSerializer.Serialize(new
                    {
                        report_id = report.ReportId,
                        title = finding.Title,
                        detail = finding.Detail,
                    }),
                    ObservedAt = report.CreatedAt,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incident report {ReportId}: failed to record observation for {Rule}", report.ReportId, finding.RuleId);
            }
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
