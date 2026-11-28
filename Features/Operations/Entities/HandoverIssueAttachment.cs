using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("handover_issue_attachments")]
public class HandoverIssueAttachment
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("handover_issue_id")]
    public Guid HandoverIssueId { get; set; }

    /// <summary>Null = ảnh chụp chung cho cả đợt lỗi; có giá trị = ảnh chụp riêng cho máy lỗi cụ thể.</summary>
    [Column("handover_issue_item_id")]
    public Guid? HandoverIssueItemId { get; set; }

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }
}
