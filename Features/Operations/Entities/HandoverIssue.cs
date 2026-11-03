using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("handover_issues")]
public class HandoverIssue
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("handover_note_id")]
    public Guid HandoverNoteId { get; set; }

    [Column("description")]
    [Required]
    public string Description { get; set; } = null!;

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public HandoverIssueStatus Status { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("resolved_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ResolvedAt { get; set; }

}
