using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Entities;

[Table("refresh_tokens")]
public class RefreshToken
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("token_hash")]
    [MaxLength(64)]
    [Required]
    public string TokenHash { get; set; } = null!;

    [Column("family_id")]
    public Guid FamilyId { get; set; }

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("revoked_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? RevokedAt { get; set; }

    [Column("replaced_by_token_id")]
    public Guid? ReplacedByTokenId { get; set; }

}
