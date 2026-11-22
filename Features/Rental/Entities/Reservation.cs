using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("reservations")]
public class Reservation
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("reservation_code")]
    [MaxLength(50)]
    [Required]
    public string ReservationCode { get; set; } = null!;

    [Column("company_id")]
    public Guid CompanyId { get; set; }

    [Column("requested_from")]
    public DateOnly RequestedFrom { get; set; }

    [Column("requested_to")]
    public DateOnly RequestedTo { get; set; }

    [Column("contact_name")]
    [MaxLength(150)]
    [Required]
    public string ContactName { get; set; } = null!;

    [Column("contact_phone")]
    [MaxLength(20)]
    [Required]
    public string ContactPhone { get; set; } = null!;

    // Hạn giữ chỗ; quá hạn mà chưa tạo hợp đồng thì chuyển sang EXPIRED.
    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public ReservationStatus Status { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
