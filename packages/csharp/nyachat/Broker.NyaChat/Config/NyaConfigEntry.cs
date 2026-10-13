using BaseOrm;

namespace Broker.NyaChat;

// ─── nya_config ────────────────────────────────────────────────────────────────
/// 可重載配置的 DB 持久化載體。
/// 單列（以 <see cref="ConfigKey"/> 為主鍵）存放序列化後的 <see cref="NyaChatConfig"/> JSON。
/// appsettings 的 NyaChat 段作為首次啟動 seed；本表存在覆寫時以本表為準。
/// </summary>
[Table("nya_config")]
public class NyaConfigEntry
{
    [Key(AutoIncrement = false)]
    [Column("config_key")]
    public string ConfigKey { get; set; } = "";

    /// <summary>序列化後的配置內容（application/json）</summary>
    [Column("config_json")]
    public string ConfigJson { get; set; } = "";

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
