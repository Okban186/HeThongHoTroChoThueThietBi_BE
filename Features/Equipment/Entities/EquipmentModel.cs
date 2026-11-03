using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Entities;

[Table("equipment_models")]
public class EquipmentModel
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("code")]
    [MaxLength(50)]
    [Required]
    public string Code { get; set; } = null!;

    [Column("name")]
    [MaxLength(255)]
    [Required]
    public string Name { get; set; } = null!;

    [Column("manufacturer")]
    [MaxLength(150)]
    public string? Manufacturer { get; set; }

    [Column("model_number")]
    [MaxLength(100)]
    public string? ModelNumber { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("estimated_installation_minutes")]
    public int EstimatedInstallationMinutes { get; set; }

    [Column("specifications", TypeName = "jsonb")]
    public string? Specifications { get; set; }

    [Column("unit")]
    [MaxLength(30)]
    [Required]
    public string Unit { get; set; } = null!;

    [Column("rental_price_daily")]
    [Precision(18, 2)]
    public decimal RentalPriceDaily { get; set; }

    [Column("rental_price_monthly")]
    [Precision(18, 2)]
    public decimal RentalPriceMonthly { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public EquipmentModelStatus Status { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
