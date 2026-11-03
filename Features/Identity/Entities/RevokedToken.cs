using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Entities;

[Table("revoked_tokens")]
public class RevokedToken
{
    [Key]
    [Column("jti")]
    public Guid Jti { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("revoked_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset RevokedAt { get; set; }

}
