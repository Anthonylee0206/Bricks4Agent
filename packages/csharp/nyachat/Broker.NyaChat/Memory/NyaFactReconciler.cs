using System.Text.Json;

namespace Broker.NyaChat;

/// <summary>調和操作守衛設定。add 低門檻（治稀疏）、retire 高門檻（防誤刪）、op 數上限（防失控）。</summary>
public sealed record NyaFactReconcileOptions(double AddMinConfidence, double RetireMinConfidence, int MaxOps);

/// <summary>單一調和操作。op ∈ add|update|retire。retire 不需 value。</summary>
public sealed record NyaFactOp(string Op, string Category, string Key, string Value, double Confidence, string Reason);

/// <summary>
/// Fact 調和解析器（純函式）。把 LLM 回的操作清單 JSON 解析為受守衛過濾的 <see cref="NyaFactOp"/>：
/// 未知 op / 壞 JSON / 缺欄位 → 跳過；instruction 類的 retire → 丟棄（保護顯式指令）；
/// add/update 低於 AddMinConfidence、retire 低於 RetireMinConfidence → 丟棄；總數截到 MaxOps。
/// </summary>
public static class NyaFactReconciler
{
    /// <summary>預設守衛：add≥0.6、retire≥0.85、單輪≤20 ops。</summary>
    public static readonly NyaFactReconcileOptions Defaults = new(0.6, 0.85, 20);

    private static readonly HashSet<string> KnownOps = new(StringComparer.OrdinalIgnoreCase) { "add", "update", "retire" };

    public static List<NyaFactOp> Parse(string raw, NyaFactReconcileOptions opts)
    {
        var result = new List<NyaFactOp>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        // 容忍前後雜訊：抓第一個 '[' 到最後一個 ']'。
        var start = raw.IndexOf('['); var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        JsonElement root;
        try { using var doc = JsonDocument.Parse(raw[start..(end + 1)]); root = doc.RootElement.Clone(); }
        catch { return result; }
        if (root.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in root.EnumerateArray())
        {
            if (result.Count >= opts.MaxOps) break;
            if (item.ValueKind != JsonValueKind.Object) continue;

            var op = Str(item, "op").ToLowerInvariant();
            if (!KnownOps.Contains(op)) continue;

            var category = Str(item, "category");
            var key = Str(item, "key");
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (op != "retire" && string.IsNullOrWhiteSpace(Str(item, "value"))) continue;

            var conf = Num(item, "confidence");

            // 守衛：保護 instruction 類不被自動 retire。
            if (op == "retire" && string.Equals(category, "instruction", StringComparison.OrdinalIgnoreCase)) continue;
            // 守衛：不對稱信心門檻。
            var floor = op == "retire" ? opts.RetireMinConfidence : opts.AddMinConfidence;
            if (conf < floor) continue;

            result.Add(new NyaFactOp(op, category, key, Str(item, "value"), conf, Str(item, "reason")));
        }
        return result;
    }

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0.0;
}
