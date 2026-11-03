using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("handover_items")]
public class HandoverItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("handover_note_id")]
    public Guid HandoverNoteId { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
