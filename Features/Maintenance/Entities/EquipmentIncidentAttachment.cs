using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("equipment_incident_attachments")]
public class EquipmentIncidentAttachment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("incident_id")]
    public Guid IncidentId { get; set; }

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
