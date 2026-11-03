using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Entities;

[Table("equipment_model_categories")]
public class EquipmentModelCategory
{
    [Column("equipment_model_id")]
    public Guid EquipmentModelId { get; set; }

    [Column("category_id")]
    public Guid CategoryId { get; set; }

}
