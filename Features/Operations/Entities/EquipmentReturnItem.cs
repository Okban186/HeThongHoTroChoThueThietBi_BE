using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("equipment_return_items")]
public class EquipmentReturnItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("equipment_return_id")]
    public Guid EquipmentReturnId { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public EquipmentReturnItemStatus Status { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
