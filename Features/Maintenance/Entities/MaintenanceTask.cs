using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("maintenance_tasks")]
public class MaintenanceTask
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("service_assignment_id")]
    public Guid? ServiceAssignmentId { get; set; }

    [Column("type")]
    [MaxLength(20)]
    [Required]
    public string Type { get; set; } = null!;

    [Column("description")]
    [Required]
    public string Description { get; set; } = null!;

    [Column("scheduled_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ScheduledAt { get; set; }

    [Column("started_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? StartedAt { get; set; }

    [Column("completed_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public MaintenanceTaskStatus Status { get; set; }

    [Column("result")]
    public string? Result { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
