namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 可重載配置存取契約。各服務改讀「當前快照」而非注入一個死的 config 物件，
/// 以支撐 LLM 設定檔（Profiles）與 Token 預算所需的「由 API 動態修改、免重啟」。
/// </summary>
/// <remarks>
/// 這是動態配置的硬前置：沒有可重載 holder，則配置只能做到「改 appsettings + 重啟」。
/// 包含持久化（SQLite）與快照替換（Snapshot Swap）的完整 hot-reload 實作。
/// </remarks>
/// <typeparam name="T">配置型別（如 <c>NyaChatConfig</c>）。</typeparam>
public interface INyaConfigStore<T> where T : class
{
    /// <summary>取得當前生效的配置快照（每次讀取反映最新一次 <see cref="UpdateAsync"/> 的結果）。</summary>
    T Current { get; }

    /// <summary>以新配置覆寫並持久化（DB / 檔案），使後續 <see cref="Current"/> 取得新快照。</summary>
    Task UpdateAsync(T config, CancellationToken cancellationToken);
}
