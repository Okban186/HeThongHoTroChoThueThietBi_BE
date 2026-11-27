using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("equipment_disposals")]
public class EquipmentDisposal
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("reason")]
    [Required]
    public string Reason { get; set; } = null!;

    [Column("disposal_date")]
    public DateOnly DisposalDate { get; set; }

    [Column("method")]
    [MaxLength(20)]
    [Required]
    public string Method { get; set; } = null!;

    [Column("amount")]
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Column("approved_by")]
    public Guid? ApprovedBy { get; set; }

    [Column("requested_by")]
    public Guid? RequestedBy { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public string Status { get; set; } = "PENDING";

    [Column("approved_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ApprovedAt { get; set; }

    [Column("rejection_reason")]
    public string? RejectionReason { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
