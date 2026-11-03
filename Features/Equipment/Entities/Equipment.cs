using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Entities;

[Table("equipments")]
public class Equipment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("equipment_model_id")]
    public Guid EquipmentModelId { get; set; }

    [Column("warehouse_id")]
    public Guid WarehouseId { get; set; }

    [Column("serial_number")]
    [MaxLength(100)]
    [Required]
    public string SerialNumber { get; set; } = null!;

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public EquipmentStatus Status { get; set; }

    [Column("condition")]
    [MaxLength(20)]
    [Required]
    public string Condition { get; set; } = null!;

    [Column("acquired_at")]
    public DateOnly? AcquiredAt { get; set; }

    [Column("acquisition_cost")]
    [Precision(18, 2)]
    public decimal? AcquisitionCost { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
