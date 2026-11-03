using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("contract_status_history")]
public class ContractStatusHistory
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("status")]
    [MaxLength(30)]
    [Required]
    public RentalContractStatus Status { get; set; }

    [Column("previous_status")]
    [MaxLength(30)]
    [Required]
    public RentalContractStatus PreviousStatus { get; set; }

    [Column("changed_by")]
    public Guid ChangedBy { get; set; }

    [Column("changed_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ChangedAt { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
