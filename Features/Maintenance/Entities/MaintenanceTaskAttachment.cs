using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("maintenance_task_attachments")]
public class MaintenanceTaskAttachment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("maintenance_task_id")]
    public Guid MaintenanceTaskId { get; set; }

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
