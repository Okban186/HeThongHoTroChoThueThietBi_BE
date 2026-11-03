using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Entities;

[Table("rental_charges")]
public class RentalCharge
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("source_type")]
    [MaxLength(20)]
    [Required]
    public string SourceType { get; set; } = null!;

    [Column("rental_contract_id")]
    public Guid? RentalContractId { get; set; }

    [Column("equipment_id")]
    public Guid? EquipmentId { get; set; }

    [Column("charge_type")]
    [MaxLength(30)]
    [Required]
    public string ChargeType { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("quantity")]
    [Precision(18, 2)]
    public decimal Quantity { get; set; }

    [Column("unit_price")]
    [Precision(18, 2)]
    public decimal UnitPrice { get; set; }

    [Column("amount")]
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Column("period_start")]
    public DateOnly? PeriodStart { get; set; }

    [Column("period_end")]
    public DateOnly? PeriodEnd { get; set; }

    [Column("due_date")]
    public DateOnly? DueDate { get; set; }

    [Column("paid_amount")]
    [Precision(18, 2)]
    public decimal PaidAmount { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public RentalChargeStatus Status { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("paid_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? PaidAt { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
