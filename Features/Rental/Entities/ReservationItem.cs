using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("reservation_items")]
public class ReservationItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("reservation_id")]
    public Guid ReservationId { get; set; }

    [Column("equipment_model_id")]
    public Guid EquipmentModelId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("note")]
    public string? Note { get; set; }

}
