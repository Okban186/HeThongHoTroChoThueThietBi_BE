using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("rental_contract_services")]
public class RentalContractService
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("service_type")]
    [MaxLength(30)]
    [Required]
    public string ServiceType { get; set; } = null!;

    [Column("provided_by")]
    [MaxLength(30)]
    [Required]
    public string ProvidedBy { get; set; } = null!;

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("unit_price")]
    [Precision(18, 2)]
    public decimal UnitPrice { get; set; }

    [Column("amount")]
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
