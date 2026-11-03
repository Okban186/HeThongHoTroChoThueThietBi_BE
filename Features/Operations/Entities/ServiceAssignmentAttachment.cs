using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("service_assignment_attachments")]
public class ServiceAssignmentAttachment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("service_assignment_id")]
    public Guid ServiceAssignmentId { get; set; }

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
