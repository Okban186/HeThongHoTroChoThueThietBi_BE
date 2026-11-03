using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("maintenance_records")]
public class MaintenanceRecord
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("maintenance_task_id")]
    public Guid MaintenanceTaskId { get; set; }

    [Column("performed_by")]
    public Guid PerformedBy { get; set; }

    [Column("description")]
    [Required]
    public string Description { get; set; } = null!;

    [Column("labor_cost")]
    [Precision(18, 2)]
    public decimal LaborCost { get; set; }

    [Column("completed_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
