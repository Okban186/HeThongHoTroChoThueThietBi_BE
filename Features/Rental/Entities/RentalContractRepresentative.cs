using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("rental_contract_representatives")]
public class RentalContractRepresentative
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("full_name")]
    [MaxLength(150)]
    [Required]
    public string FullName { get; set; } = null!;

    [Column("id_number")]
    [MaxLength(50)]
    [Required]
    public string IdNumber { get; set; } = null!;

    [Column("phone")]
    [MaxLength(20)]
    public string? Phone { get; set; }

    [Column("email")]
    [MaxLength(255)]
    public string? Email { get; set; }

    [Column("representative_type")]
    [MaxLength(30)]
    [Required]
    public string RepresentativeType { get; set; } = null!;

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
