using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("handover_note_attachments")]
public class HandoverNoteAttachment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("handover_note_id")]
    public Guid HandoverNoteId { get; set; }

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
