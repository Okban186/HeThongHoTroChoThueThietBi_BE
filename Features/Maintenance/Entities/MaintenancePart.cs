using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("maintenance_parts")]
public class MaintenancePart
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("maintenance_record_id")]
    public Guid MaintenanceRecordId { get; set; }

    [Column("name")]
    [MaxLength(255)]
    [Required]
    public string Name { get; set; } = null!;

    [Column("quantity")]
    [Precision(18, 2)]
    public decimal Quantity { get; set; }

    [Column("unit_cost")]
    [Precision(18, 2)]
    public decimal UnitCost { get; set; }

    [Column("total_cost")]
    [Precision(18, 2)]
    public decimal TotalCost { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
