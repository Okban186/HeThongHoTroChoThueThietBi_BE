using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("replacement_request_items")]
public class ReplacementRequestItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("replacement_request_id")]
    public Guid ReplacementRequestId { get; set; }

    [Column("old_equipment_id")]
    public Guid OldEquipmentId { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
