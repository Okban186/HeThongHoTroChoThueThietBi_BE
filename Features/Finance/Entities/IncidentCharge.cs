using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using System;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Entities;

[Table("incident_charges")]
public class IncidentCharge
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("incident_item_id")]
    public Guid IncidentItemId { get; set; }

    [Column("charge_type")]
    [MaxLength(20)]
    [Required]
    public string ChargeType { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("amount")]
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Column("status")]
    [MaxLength(30)]
    [Required]
    public IncidentChargeStatus Status { get; set; }

    [Column("rental_charge_id")]
    public Guid? RentalChargeId { get; set; }

    [Column("proposed_by")]
    public Guid? ProposedBy { get; set; }

    [Column("approved_by")]
    public Guid? ApprovedBy { get; set; }

    [Column("approved_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>OFFSET_DEPOSIT | DIRECT_BILLING | WAIVED</summary>
    [Column("settlement_method")]
    [MaxLength(30)]
    public string? SettlementMethod { get; set; }

    [Column("deposit_offset_amount")]
    [Precision(18, 2)]
    public decimal DepositOffsetAmount { get; set; }

    [Column("manager_note")]
    public string? ManagerNote { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }
}
