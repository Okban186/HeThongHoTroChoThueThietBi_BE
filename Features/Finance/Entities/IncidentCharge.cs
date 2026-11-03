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
    [MaxLength(20)]
    [Required]
    public IncidentChargeStatus Status { get; set; }

    [Column("rental_charge_id")]
    public Guid? RentalChargeId { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
