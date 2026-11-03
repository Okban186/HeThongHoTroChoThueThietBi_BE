using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("handover_issue_items")]
public class HandoverIssueItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("handover_issue_id")]
    public Guid HandoverIssueId { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("issue_type")]
    [MaxLength(30)]
    [Required]
    public string IssueType { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public HandoverIssueItemStatus Status { get; set; }

}
