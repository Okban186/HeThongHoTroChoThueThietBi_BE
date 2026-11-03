using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Organization.Entities;

[Table("companies")]
public class Company
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("company_code")]
    [MaxLength(50)]
    [Required]
    public string CompanyCode { get; set; } = null!;

    [Column("name")]
    [MaxLength(255)]
    [Required]
    public string Name { get; set; } = null!;

    [Column("tax_code")]
    [MaxLength(50)]
    [Required]
    public string TaxCode { get; set; } = null!;

    [Column("address")]
    public string? Address { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public CompanyStatus Status { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
