using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Notification.Entities;

[Table("notifications")]
public class Notification
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("title")]
    [MaxLength(255)]
    [Required]
    public string Title { get; set; } = null!;

    [Column("content")]
    [Required]
    public string Content { get; set; } = null!;

    [Column("type")]
    [MaxLength(50)]
    public string? Type { get; set; }

    [Column("is_read")]
    public bool IsRead { get; set; }

    [Column("read_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ReadAt { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
