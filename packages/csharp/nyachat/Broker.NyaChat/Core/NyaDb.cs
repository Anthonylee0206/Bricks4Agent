using BrokerCore.Data;

namespace Broker.NyaChat;

/// <summary>
/// NyaChat 專屬資料庫 holder（2026-06-13 資料隔離原則：broker 不過 AI 產出）。
/// </summary>
/// <remarks>
/// 之前 Nya 的持久化服務直接吃宿主共用的 <see cref="BrokerDb"/>（broker.db），AI 產出
/// （對話/記憶/稽核）與治理資料混庫——違反「broker 是控制平面」的定位，也讓 broker.db
/// 無法隨意重建。改為 <see cref="NyaChatConfig.DbPath"/> 指定的獨立 SQLite，
/// 由 <c>AddNyaChat</c> 註冊本 holder，三個 DB 服務（Memory / Audit / ConfigStore）
/// 經 factory 取 <see cref="Db"/>——服務建構式簽章不變（仍收 BrokerDb），測試零改動。
/// <para>這同時是未來抽離（Discord bot）的儲存自主權：NyaChat 自帶完整資料庫。</para>
/// </remarks>
public sealed class NyaDb : IDisposable
{
    public BrokerDb Db { get; }

    public NyaDb(NyaChatConfig seed, ILogger<NyaDb> logger)
    {
        var path = string.IsNullOrWhiteSpace(seed.DbPath) ? "nyachat.db" : seed.DbPath;
        Db = BrokerDb.UseSqlite($"Data Source={path}");
        logger.LogInformation("[NyaDb] NyaChat database path: {Path}", path);
    }

    public void Dispose() => Db.Dispose();
}
