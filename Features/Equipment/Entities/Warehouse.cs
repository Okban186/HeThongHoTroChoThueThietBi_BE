using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Entities;

[Table("warehouses")]
public class Warehouse
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("code")]
    [MaxLength(50)]
    [Required]
    public string Code { get; set; } = null!;

    [Column("name")]
    [MaxLength(150)]
    [Required]
    public string Name { get; set; } = null!;

    [Column("address")]
    public string? Address { get; set; }

    [Column("phone")]
    [MaxLength(20)]
    public string? Phone { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public WarehouseStatus Status { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
