using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("equipment_incidents")]
public class EquipmentIncident
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("incident_code")]
    [MaxLength(50)]
    [Required]
    public string IncidentCode { get; set; } = null!;

    [Column("company_id")]
    public Guid CompanyId { get; set; }

    [Column("description")]
    [Required]
    public string Description { get; set; } = null!;

    [Column("reported_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ReportedAt { get; set; }

    [Column("resolved_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ResolvedAt { get; set; }

    [Column("status")]
    [MaxLength(30)]
    [Required]
    public EquipmentIncidentStatus Status { get; set; }

    [Column("resolution")]
    public string? Resolution { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
