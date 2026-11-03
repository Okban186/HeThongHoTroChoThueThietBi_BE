using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Entities;

[Table("roles")]
public class Role
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("code")]
    [MaxLength(50)]
    [Required]
    public string Code { get; set; } = null!;

    [Column("name")]
    [MaxLength(100)]
    [Required]
    public string Name { get; set; } = null!;

    [Column("description")]
    public string? Description { get; set; }

}
