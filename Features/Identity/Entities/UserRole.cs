using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Entities;

[Table("user_roles")]
public class UserRole
{
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("role_id")]
    public Guid RoleId { get; set; }

    [Column("assigned_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset AssignedAt { get; set; }

}
