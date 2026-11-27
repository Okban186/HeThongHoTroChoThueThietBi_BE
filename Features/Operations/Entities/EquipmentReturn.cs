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

    /// <summary>Chuyến công tác RETRIEVAL cử đội KTV đi thu hồi thiết bị tận nơi (NULL nếu trả trực tiếp tại kho).</summary>
    [Column("service_assignment_id")]
    public Guid? ServiceAssignmentId { get; set; }

    [Column("stop_order")]
    public int StopOrder { get; set; } = 1;

    [Column("pickup_latitude")]
    public double? PickupLatitude { get; set; }

    [Column("pickup_longitude")]
    public double? PickupLongitude { get; set; }

    [Column("estimated_travel_minutes")]
    public int EstimatedTravelMinutes { get; set; }

    [Column("estimated_uninstall_minutes")]
    public int EstimatedUninstallMinutes { get; set; }

    [Column("returned_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ReturnedAt { get; set; }

    [Column("received_by")]
    public Guid ReceivedBy { get; set; }

    /// <summary>Thủ kho đã tiếp nhận xử lý phiếu thu hồi này (NULL = chưa ai nhận, tất cả Thủ kho đều thấy).</summary>
    [Column("assigned_storekeeper_id")]
    public Guid? AssignedStorekeeperId { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public EquipmentReturnStatus Status { get; set; }

    [Column("return_type")]
    [MaxLength(30)]
    [Required]
    public string ReturnType { get; set; } = "NORMAL";

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }
}
