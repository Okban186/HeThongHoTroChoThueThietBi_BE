using HeThongHoTroChoThueThietBi.Features.Identity.Enums;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Entities;

[Table("users")]
public class User
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("username")]
    [MaxLength(100)]
    [Required]
    public string Username { get; set; } = null!;

    [Column("password_hash")]
    [Required]
    public string PasswordHash { get; set; } = null!;

    [Column("full_name")]
    [MaxLength(150)]
    [Required]
    public string FullName { get; set; } = null!;

    [Column("email")]
    [MaxLength(255)]
    public string? Email { get; set; }

    [Column("phone")]
    [MaxLength(20)]
    public string? Phone { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public UserStatus Status { get; set; }

    public Guid? AvatarStorageFileId { get; set; }

    public StorageFile? AvatarStorageFile { get; set; }

    [Column("last_login_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? LastLoginAt { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
