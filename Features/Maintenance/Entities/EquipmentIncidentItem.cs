using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("equipment_incident_items")]
public class EquipmentIncidentItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("incident_id")]
    public Guid IncidentId { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("assessment")]
    public string? Assessment { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
