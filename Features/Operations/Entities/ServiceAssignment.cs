using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("service_assignments")]
public class ServiceAssignment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("type")]
    [MaxLength(30)]
    [Required]
    public string Type { get; set; } = null!;

    [Column("title")]
    [MaxLength(255)]
    [Required]
    public string Title { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public ServiceAssignmentStatus Status { get; set; }

    [Column("assigned_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? AssignedAt { get; set; }

    [Column("scheduled_start_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ScheduledStartAt { get; set; }

    [Column("estimated_duration_minutes")]
    public int? EstimatedDurationMinutes { get; set; }

    [Column("started_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? StartedAt { get; set; }

    [Column("completed_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? CompletedAt { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
