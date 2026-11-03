using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("rental_contract_items")]
public class RentalContractItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("equipment_model_id")]
    public Guid EquipmentModelId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("billing_unit")]
    [MaxLength(20)]
    [Required]
    public string BillingUnit { get; set; } = null!;

    [Column("agreed_unit_price")]
    [Precision(18, 2)]
    public decimal AgreedUnitPrice { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
