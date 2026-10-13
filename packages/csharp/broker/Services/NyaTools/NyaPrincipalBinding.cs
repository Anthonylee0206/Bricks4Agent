using BaseOrm;

namespace Broker.Services.NyaTools;

/// <summary>
/// Nya 使用者 → broker Principal 的身分映射（寄宿功課 Phase 0-A）。
/// </summary>
/// <remarks>
/// NyaChat 的 userId（LINE U-id / Discord id 等）與 broker 治理層的 <c>principals</c> 是兩套
/// 身分空間；具名權限工具（如 quant.*）必須先經此表把對話身分換成 Principal，
/// 才能沿 <c>role_bindings → roles.default_capability_ids</c> 判權。
/// <para>
/// 綁定本身就是權限授予，<b>只能由管理端建立</b>，不開放 LLM 工具自行綁定。
/// 無綁定 = 無任何具名權限（安全預設，與綁定表不存在時行為一致）。
/// channel_type 為 <c>"*"</c> 表示綁定適用所有通道；解析時精確通道優先於萬用。
/// 撤銷採 soft-delete（revoked_at），保留授予/撤銷的完整軌跡。
/// </para>
/// </remarks>
[Table("nya_principal_bindings")]
public class NyaPrincipalBinding
{
    [Key(AutoIncrement = false)]
    [Column("binding_id")]
    [MaxLength(40)]
    public string BindingId { get; set; } = string.Empty;

    /// <summary>Nya 側使用者識別（與 nya_messages.user_id 同空間）</summary>
    [Column("user_id")]
    [Required]
    [MaxLength(80)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>適用通道（"line" / "discord" / …；"*" = 全通道）</summary>
    [Column("channel_type")]
    [Required]
    [MaxLength(20)]
    public string ChannelType { get; set; } = "*";

    [Column("principal_id")]
    [Required]
    [MaxLength(64)]
    public string PrincipalId { get; set; } = string.Empty;

    /// <summary>建立者（管理端身分，審計用）</summary>
    [Column("created_by")]
    [MaxLength(64)]
    public string CreatedBy { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>非 null = 已撤銷（soft-delete，保留軌跡）</summary>
    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }
}
