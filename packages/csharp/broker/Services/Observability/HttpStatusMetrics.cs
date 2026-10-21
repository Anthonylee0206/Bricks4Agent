using System.Collections.Concurrent;

namespace Broker.Services.Observability;

/// <summary>
/// API 回應狀態碼的記憶體統計，保留 24 小時。
///
/// 為什麼需要它：main 的中介層順序是「加密 → worker 驗證 → 身分驗證 → 稽核」，稽核排在最後，
/// 所以被前面幾層擋下的請求（驗證失敗的 401、加密層的 400、速率限制的 429、body 過大的 413）
/// 都不會寫進 audit_events。<see cref="Broker.Middleware.HttpStatusMetricsMiddleware"/> 掛在管線最外層，
/// 每個 /api/ 回應都算得到。
///
/// 只有狀態碼 ≥ 400 的回應以「秒」為單位記錄（數量少，時間窗邊界才準）；
/// 全部回應只記每分鐘總數，給報告的涵蓋範圍用。
///
/// 防止記憶體被灌爆：
///   - 以「路由比對到的範本」分組（例如 /api/v1/user/approvals/{approvalId}/approve），組數最多就是端點數量；
///     比對不到任何端點的路徑（亂打的網址）全部歸成同一組，攻擊者用隨機路徑也只會累加同一組的計數；
///   - 錯誤統計的組數有上限，超過時新的組合不記，只累計漏記筆數，報告會寫明。
/// 代價：資料在記憶體，broker 重啟後歸零，報告上會寫明。
/// </summary>
public sealed class HttpStatusMetrics
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public const int DefaultMaxErrorEntries = 20_000;
    public const string UnmatchedRoute = "(no matching route)";

    private readonly ConcurrentDictionary<(long Second, string Route, int Status), int> _errors = new();
    private readonly ConcurrentDictionary<long, int> _totalsPerMinute = new();
    private readonly int _maxErrorEntries;
    private long _errorEntries;
    private long _droppedErrors;
    private long _lastPruneSecond;

    public HttpStatusMetrics() : this(DefaultMaxErrorEntries)
    {
    }

    public HttpStatusMetrics(int maxErrorEntries)
    {
        _maxErrorEntries = Math.Max(1, maxErrorEntries);
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>因為達到組數上限而沒有記到的錯誤回應筆數（broker 啟動以來）。</summary>
    public long DroppedErrors => Interlocked.Read(ref _droppedErrors);

    /// <param name="routeTemplate">路由比對到的端點範本；比對不到時傳 null。</param>
    public void Record(string? routeTemplate, int statusCode, DateTime utcNow)
    {
        var second = utcNow.Ticks / TimeSpan.TicksPerSecond;
        _totalsPerMinute.AddOrUpdate(second / 60, 1, (_, n) => n + 1);

        if (statusCode >= 400)
        {
            var key = (second, string.IsNullOrWhiteSpace(routeTemplate) ? UnmatchedRoute : routeTemplate, statusCode);
            if (_errors.ContainsKey(key))
            {
                _errors.AddOrUpdate(key, 1, (_, n) => n + 1);
            }
            else if (Interlocked.Read(ref _errorEntries) < _maxErrorEntries && _errors.TryAdd(key, 1))
            {
                Interlocked.Increment(ref _errorEntries);
            }
            else if (_errors.ContainsKey(key))
            {
                // 另一個執行緒剛好先加了同一組
                _errors.AddOrUpdate(key, 1, (_, n) => n + 1);
            }
            else
            {
                Interlocked.Increment(ref _droppedErrors);
            }
        }

        PruneIfDue(second);
    }

    /// <summary>[start, end) 內狀態碼 ≥ 400 的回應，依路由範本與狀態碼分組。</summary>
    public List<HttpStatusCount> QueryErrors(DateTime start, DateTime end)
    {
        var from = start.Ticks / TimeSpan.TicksPerSecond;
        var to = end.Ticks / TimeSpan.TicksPerSecond;
        return _errors
            .Where(kv => kv.Key.Second >= from && kv.Key.Second < to)
            .GroupBy(kv => (kv.Key.Route, kv.Key.Status))
            .Select(g => new HttpStatusCount(g.Key.Route, g.Key.Status, g.Sum(kv => kv.Value)))
            .OrderByDescending(c => c.Count)
            .ToList();
    }

    /// <summary>[start, end) 內的回應總數（以分鐘計，邊界分鐘整筆計入，只給涵蓋範圍參考）。</summary>
    public int CountResponses(DateTime start, DateTime end)
    {
        var from = start.Ticks / TimeSpan.TicksPerMinute;
        var to = (end.Ticks - 1) / TimeSpan.TicksPerMinute;
        return _totalsPerMinute.Where(kv => kv.Key >= from && kv.Key <= to).Sum(kv => kv.Value);
    }

    private void PruneIfDue(long nowSecond)
    {
        var last = Interlocked.Read(ref _lastPruneSecond);
        if (nowSecond - last < 600 || Interlocked.CompareExchange(ref _lastPruneSecond, nowSecond, last) != last)
            return;

        var cutoffSecond = nowSecond - (long)Retention.TotalSeconds;
        foreach (var key in _errors.Keys)
        {
            if (key.Second < cutoffSecond && _errors.TryRemove(key, out _))
                Interlocked.Decrement(ref _errorEntries);
        }

        foreach (var minute in _totalsPerMinute.Keys)
        {
            if (minute < cutoffSecond / 60)
                _totalsPerMinute.TryRemove(minute, out _);
        }
    }
}
