using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Entities;

[Table("equipment_model_media")]
public class EquipmentModelMedia
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("equipment_model_id")]
    public Guid EquipmentModelId { get; set; }

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("media_type")]
    [MaxLength(20)]
    [Required]
    public string MediaType { get; set; } = null!;

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("is_primary")]
    public bool IsPrimary { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
