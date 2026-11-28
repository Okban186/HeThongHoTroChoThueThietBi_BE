using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

public sealed record TechnicianMediaAttachmentDto(
    Guid StorageFileId,
    string Url,
    string MediaType, // IMAGE | VIDEO
    string? FileName,
    long? SizeBytes
);

public sealed record TechnicianDashboardResponse(
    int PendingTripsCount,
    int InProgressTripsCount,
    int CompletedTripsTodayCount,
    int PendingMaintenanceCount,
    int InProgressMaintenanceCount,
    int OpenIncidentsCount,
    List<TechnicianAssignmentSummaryResponse> ActiveTrips
);

public sealed record TechnicianDepotDto(
    string WarehouseName,
    string? Address,
    double? Latitude,
    double? Longitude
);

public sealed record TechnicianAssignmentMemberDto(
    Guid StaffId,
    string EmployeeCode,
    string FullName,
    string? Phone,
    string Role // LEADER | MEMBER
);

public sealed record TechnicianHandoverStopSummaryDto(
    Guid HandoverNoteId,
    string HandoverCode,
    string HandoverType, // INITIAL | SUPPLEMENTARY
    Guid RentalContractId,
    string ContractCode,
    string CompanyName,
    string? DeliveryAddress,
    double? DeliveryLatitude,
    double? DeliveryLongitude,
    string ReceivedByName,
    string? ReceivedByPhone,
    int StopOrder,
    int EstimatedTravelMinutes,
    int EstimatedInstallMinutes,
    string Status,
    int ItemCount,
    int DeliveryAttemptNumber,
    string? FailedReason,
    DateTimeOffset? FailedAt
);

public sealed record TechnicianAssignmentSummaryResponse(
    Guid AssignmentId,
    string Type, // HANDOVER | INCIDENT | MAINTENANCE | RETRIEVAL
    string Title,
    string? Description,
    string Status,
    bool IsLeader,
    DateTimeOffset? ScheduledStartAt,
    int? EstimatedDurationMinutes,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int StopCount,
    int CompletedStopCount,
    List<TechnicianAssignmentMemberDto> Members,
    List<TechnicianHandoverStopSummaryDto> Stops
);

public sealed record TechnicianEquipmentGroupDto(
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    int Quantity,
    List<TechnicianEquipmentItemDto> Items
);

public sealed record TechnicianEquipmentItemDto(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    string Condition,
    string Status,
    string? Note
);

public sealed record TechnicianFirstPaymentInfoDto(
    Guid RentalContractId,
    string RentalType, // SHORT_TERM | LONG_TERM
    Guid? FirstInstallmentChargeId,
    string Description,
    decimal Amount,
    decimal PaidAmount,
    decimal RemainingAmount,
    string Status, // UNPAID | PARTIALLY_PAID | PAID
    bool HasPendingCash,
    decimal ShippingFeeIncluded
);

public sealed record TechnicianHandoverLookupSerialResponse(
    bool BelongsToHandover,
    Guid? EquipmentId,
    string SerialNumber,
    Guid? EquipmentModelId,
    string? ModelCode,
    string? ModelName,
    string? Condition,
    string? Message
);

public sealed record TechnicianDefectiveIssueItemDetailDto(
    Guid IssueItemId,
    Guid EquipmentId,
    string SerialNumber,
    string ModelCode,
    string ModelName,
    string IssueType,
    string? Description,
    string Status,
    List<TechnicianMediaAttachmentDto> ItemPhotos
);

public sealed record TechnicianHandoverResultDetailDto(
    int DeliveredGoodCount,
    int DefectiveCount,
    string? IssueDescription,
    Guid? AutoReturnId,
    string? AutoReturnCode,
    string? AutoReturnStatus,
    List<TechnicianMediaAttachmentDto> SceneMedia,
    List<TechnicianMediaAttachmentDto> SharedIssueMedia,
    List<TechnicianEquipmentItemDto> DeliveredGoodItems,
    List<TechnicianDefectiveIssueItemDetailDto> DefectiveItems
);

public sealed record TechnicianHandoverStopDetailDto(
    Guid HandoverNoteId,
    string HandoverCode,
    string HandoverType,
    Guid RentalContractId,
    string ContractCode,
    string ContractStatus,
    string CompanyName,
    string? DeliveryAddress,
    double? DeliveryLatitude,
    double? DeliveryLongitude,
    string? DeliveryNote,
    string ReceivedByName,
    string? ReceivedByPhone,
    int StopOrder,
    int EstimatedTravelMinutes,
    int EstimatedInstallMinutes,
    string Status,
    int DeliveryAttemptNumber,
    string? FailedReason,
    DateTimeOffset? FailedAt,
    string? Note,
    List<TechnicianEquipmentGroupDto> EquipmentGroups,
    List<string> EvidenceUrls,
    TechnicianFirstPaymentInfoDto? FirstPayment,
    List<TechnicianMediaAttachmentDto>? EvidenceMedia = null,
    TechnicianHandoverResultDetailDto? DeliveryResult = null
);

public sealed record TechnicianAssignmentDetailResponse(
    Guid AssignmentId,
    string Type,
    string Title,
    string? Description,
    string Status,
    bool IsLeader,
    DateTimeOffset? ScheduledStartAt,
    int? EstimatedDurationMinutes,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TechnicianDepotDto? Depot,
    List<TechnicianAssignmentMemberDto> Members,
    List<TechnicianHandoverStopDetailDto> Stops,
    List<string> AssignmentAttachmentUrls,
    List<TechnicianRetrievalStopDetailDto>? RetrievalStops = null,
    List<TechnicianMediaAttachmentDto>? AssignmentMedia = null
);

public sealed record TechnicianRetrievalItemDto(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    string Condition,
    string ItemStatus, // RETURNED | MISSING
    string? Note
);

public sealed record TechnicianRetrievalStopDetailDto(
    Guid ReturnId,
    string ReturnCode,
    Guid RentalContractId,
    string ContractCode,
    string ContractStatus,
    string CompanyName,
    string? PickupAddress,
    double? PickupLatitude,
    double? PickupLongitude,
    string? ContactName,
    string? ContactPhone,
    DateOnly ContractEndDate,
    int StopOrder,
    int EstimatedTravelMinutes,
    int EstimatedUninstallMinutes,
    string Status, // DRAFT (chờ KTV thu) | PROCESSING (KTV đã thu, chờ về kho) | COMPLETED (Kho đã kiểm nhận)
    string? Note,
    List<TechnicianRetrievalItemDto> Items
);

public sealed class TechnicianCompleteRetrievalItemRequest
{
    [Required]
    public Guid? EquipmentId { get; init; }

    /// <summary>RETURNED | MISSING</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; init; } = "RETURNED";

    /// <summary>GOOD | FAIR | DAMAGED (bắt buộc khi Status = RETURNED)</summary>
    [MaxLength(20)]
    public string? Condition { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class TechnicianCompleteRetrievalRequest
{
    [MaxLength(1000)]
    public string? Note { get; init; }

    /// <summary>Bắt buộc tối thiểu 1 ảnh hoặc video minh chứng hiện trường thu hồi (video tối đa 500MB).</summary>
    [Required, MinLength(1, ErrorMessage = "Vui lòng đính kèm ít nhất 1 ảnh hoặc video minh chứng thu hồi thiết bị.")]
    [MaxLength(12, ErrorMessage = "Tối đa 12 tệp minh chứng.")]
    public List<Guid> AttachmentFileIds { get; init; } = [];

    [Required, MinLength(1, ErrorMessage = "Vui lòng xác nhận danh sách thiết bị thu hồi.")]
    public List<TechnicianCompleteRetrievalItemRequest> Items { get; init; } = [];
}

public sealed record TechnicianCompleteRetrievalResponse(
    Guid ReturnId,
    string ReturnCode,
    int ReturnedCount,
    int MissingCount,
    string Message
);

public sealed class TechnicianCompleteHandoverRequest
{
    [MaxLength(1000)]
    public string? Note { get; init; }

    /// <summary>Bắt buộc tối thiểu 1 ảnh hoặc video hiện trường lắp đặt xong (video tối đa 500MB).</summary>
    [Required, MinLength(1, ErrorMessage = "Vui lòng đính kèm ít nhất 1 ảnh hoặc video minh chứng hiện trường bàn giao.")]
    [MaxLength(12, ErrorMessage = "Tối đa 12 tệp minh chứng hiện trường.")]
    public List<Guid> SceneAttachmentFileIds { get; init; } = [];

    /// <summary>Ảnh hoặc video chụp/quay chung cho cả đợt máy lỗi (video tối đa 500MB).</summary>
    [MaxLength(12, ErrorMessage = "Tối đa 12 tệp chung máy lỗi.")]
    public List<Guid> IssueAttachmentFileIds { get; init; } = [];

    /// <summary>Danh sách máy bị lỗi ngay khi giao/lắp (KTV quét/nhập mã serial để thêm).</summary>
    public List<TechnicianDefectiveItemRequest> DefectiveItems { get; init; } = [];
}

public sealed class TechnicianDefectiveItemRequest
{
    [Required]
    public Guid? EquipmentId { get; init; }

    /// <summary>DAMAGED | MALFUNCTION | OTHER</summary>
    [MaxLength(30)]
    public string IssueType { get; init; } = "DAMAGED";

    [MaxLength(500)]
    public string? Description { get; init; }

    /// <summary>Tùy chọn chụp ảnh riêng cho từng máy hư hỏng (CHỈ HỖ TRỢ ẢNH, không nhận video).</summary>
    [MaxLength(5, ErrorMessage = "Tối đa 5 ảnh riêng mỗi máy lỗi.")]
    public List<Guid> AttachmentFileIds { get; init; } = [];
}

public sealed class TechnicianFailHandoverRequest
{
    [Required(ErrorMessage = "Vui lòng nhập lý do giao không thành công.")]
    [MaxLength(1000)]
    public string Reason { get; init; } = string.Empty;

    /// <summary>Bắt buộc tối thiểu 1 ảnh hoặc video minh chứng giao thất bại (video tối đa 500MB).</summary>
    [Required, MinLength(1, ErrorMessage = "Vui lòng đính kèm ít nhất 1 ảnh hoặc video minh chứng giao không thành công.")]
    [MaxLength(8, ErrorMessage = "Tối đa 8 tệp minh chứng.")]
    public List<Guid> AttachmentFileIds { get; init; } = [];
}

public sealed record TechnicianCompleteHandoverResponse(
    Guid HandoverNoteId,
    string HandoverCode,
    string ContractStatus,
    int DeliveredGoodCount,
    int DefectiveCount,
    Guid? AutoReturnId,
    string? AutoReturnCode,
    string Message
);

public sealed record TechnicianFailHandoverResponse(
    Guid HandoverNoteId,
    string HandoverCode,
    int DeliveryAttemptNumber,
    bool PenaltyApplied,
    decimal PenaltyAmount,
    string Message
);
