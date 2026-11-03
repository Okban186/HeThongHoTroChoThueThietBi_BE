using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("equipment_returns")]
public class EquipmentReturn
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("return_code")]
    [MaxLength(50)]
    [Required]
    public string ReturnCode { get; set; } = null!;

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("returned_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ReturnedAt { get; set; }

    [Column("received_by")]
    public Guid ReceivedBy { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public EquipmentReturnStatus Status { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
