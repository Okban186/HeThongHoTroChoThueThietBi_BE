using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("handover_notes")]
public class HandoverNote
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("handover_code")]
    [MaxLength(50)]
    [Required]
    public string HandoverCode { get; set; } = null!;

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("service_assignment_id")]
    public Guid? ServiceAssignmentId { get; set; }

    [Column("handover_date")]
    public DateOnly HandoverDate { get; set; }

    [Column("replacement_request_id")]
    public Guid? ReplacementRequestId { get; set; }

    /// <summary>INITIAL (phiếu gốc) | SUPPLEMENTARY (giao bù máy lỗi khi giao) | INCIDENT_REPLACEMENT (giao máy thay thế sự cố).</summary>
    [Column("handover_type")]
    [MaxLength(30)]
    [Required]
    public string HandoverType { get; set; } = "INITIAL";

    [Column("parent_handover_id")]
    public Guid? ParentHandoverId { get; set; }

    /// <summary>Liên kết sự cố nguồn khi HandoverType = INCIDENT_REPLACEMENT.</summary>
    [Column("incident_id")]
    public Guid? IncidentId { get; set; }

    [Column("delivery_attempt_number")]
    public int DeliveryAttemptNumber { get; set; }

    [Column("stop_order")]
    public int StopOrder { get; set; } = 1;

    [Column("delivery_latitude")]
    public double? DeliveryLatitude { get; set; }

    [Column("delivery_longitude")]
    public double? DeliveryLongitude { get; set; }

    [Column("estimated_travel_minutes")]
    public int EstimatedTravelMinutes { get; set; }

    [Column("estimated_install_minutes")]
    public int EstimatedInstallMinutes { get; set; }

    [Column("approval_note")]
    public string? ApprovalNote { get; set; }

    [Column("failed_reason")]
    public string? FailedReason { get; set; }

    [Column("failed_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? FailedAt { get; set; }

    [Column("received_by_name")]
    [MaxLength(150)]
    [Required]
    public string ReceivedByName { get; set; } = null!;

    [Column("received_by_phone")]
    [MaxLength(20)]
    public string? ReceivedByPhone { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public HandoverNoteStatus Status { get; set; }

    /// <summary>Thủ kho đã tiếp nhận soạn phiếu này (NULL = chưa ai nhận, tất cả Thủ kho đều thấy).</summary>
    [Column("assigned_storekeeper_id")]
    public Guid? AssignedStorekeeperId { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
