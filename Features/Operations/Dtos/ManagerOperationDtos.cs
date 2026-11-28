using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

public sealed record TechnicianAvailabilityResponse(
    Guid StaffId,
    Guid UserId,
    string EmployeeCode,
    string FullName,
    string? Phone,
    bool IsAvailable,
    string? ConflictTitle,
    DateTimeOffset? ConflictStart,
    DateTimeOffset? ConflictEnd);

public sealed record PreviewDeliveryTripRequest(
    [Required, MinLength(1)] List<Guid> ContractIds,
    DateTimeOffset ScheduledStartAt,
    Guid? ExistingAssignmentId = null);

public sealed record CreateDeliveryTripRequest(
    [Required, MinLength(1)] List<Guid> ContractIds,
    DateTimeOffset ScheduledStartAt,
    [Required] Guid LeaderStaffId,
    List<Guid> MemberStaffIds,
    string? Title,
    string? Note);

public sealed record MergeIntoDeliveryTripRequest(
    [Required, MinLength(1)] List<Guid> ContractIds);

public sealed record UnscheduledDeliveryContractResponse(
    Guid Id,
    string ContractCode,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    string? DeliveryAddress,
    int TotalQuantity,
    Guid? ExistingHandoverId = null,
    string? ExistingHandoverCode = null,
    string HandoverType = "INITIAL",
    int FailedAttemptCount = 0,
    string? LastFailedReason = null,
    DateTimeOffset? LastFailedAt = null);

public sealed record MergeableTripResponse(
    Guid AssignmentId,
    string Title,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    int EstimatedDurationMinutes,
    string LeaderName,
    List<string> MemberNames,
    int CurrentStopCount,
    List<string> ExistingContractCodes);

public sealed record DailyScheduleStopDto(
    Guid HandoverNoteId,
    string HandoverCode,
    string ContractCode,
    string CompanyName,
    int StopOrder,
    int EstimatedTravelMinutes,
    int EstimatedInstallMinutes,
    string Status);

public sealed record DailyScheduleItemResponse(
    Guid AssignmentId,
    string Type,
    string Title,
    string? Description,
    string Status,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    int EstimatedDurationMinutes,
    string LeaderName,
    List<string> MemberNames,
    int StopCount,
    List<DailyScheduleStopDto> Stops);

public sealed record ManagerIncidentResponse(
    Guid Id,
    string IncidentCode,
    Guid CompanyId,
    string CompanyName,
    string? ContactName,
    string? ContactPhone,
    string Description,
    string Status,
    DateTimeOffset ReportedAt,
    Guid? RemoteTechnicianStaffId,
    string? RemoteTechnicianName,
    Guid? ServiceAssignmentId,
    List<string> EquipmentSerials,
    List<string> AttachmentUrls,
    string? Resolution = null);

public sealed record AssignRemoteIncidentRequest(
    [Required] Guid TechnicianStaffId,
    string? Note);

public sealed record DispatchIncidentTeamRequest(
    DateTimeOffset ScheduledStartAt,
    [Range(15, 600)] int EstimatedDurationMinutes,
    [Required] Guid LeaderStaffId,
    List<Guid> MemberStaffIds,
    string? Note);

public sealed record ApprovalDecisionRequest(
    bool Approve,
    string? Reason);

public sealed record RecordDeliveryFailureRequest(
    [Required(ErrorMessage = "Vui lòng nhập lý do giao thất bại.")]
    [MaxLength(1000)]
    string Reason,
    [Range(0, double.MaxValue)]
    decimal? PenaltyAmount = null,
    bool Reschedule = true);

public sealed record PendingReplacementItemDto(
    Guid OldEquipmentId,
    string OldSerialNumber,
    string OldModelName,
    Guid? ReplacementModelId,
    string? ReplacementModelName,
    string? Note);

public sealed record PendingReplacementResponse(
    Guid Id,
    string RequestCode,
    string ContractCode,
    string CompanyName,
    string Reason,
    string Status,
    string RequestedByName,
    DateTimeOffset CreatedAt,
    List<PendingReplacementItemDto> Items);

public sealed record PendingHandoverItemDto(
    Guid EquipmentId,
    string SerialNumber,
    string ModelCode,
    string ModelName,
    string? SubstitutedForModelName = null);

public sealed record PendingHandoverModelGroupDto(
    Guid ModelId,
    string ModelCode,
    string ModelName,
    int PreparedCount,
    int? RequiredQuantity,
    bool IsSubstitutedModel,
    string? SubstitutedForLabel,
    List<string> SerialNumbers);

public sealed record PendingHandoverResponse(
    Guid Id,
    string HandoverCode,
    string ContractCode,
    string CompanyName,
    DateOnly HandoverDate,
    string Status,
    int TotalPreparedCount,
    int TotalRequiredCount,
    List<string> RequiredContractModels,
    List<PendingHandoverModelGroupDto> ModelGroups,
    List<PendingHandoverItemDto> PreparedItems,
    bool HasModelMismatch);

public sealed record PendingDisposalResponse(
    Guid Id,
    Guid EquipmentId,
    string SerialNumber,
    string ModelName,
    string Reason,
    string Method,
    decimal Amount,
    DateOnly DisposalDate,
    string Status,
    string? RequestedByName);


/// <summary>Tổng quan các hợp đồng giao thiếu (PARTIALLY_DELIVERED) cần thu hồi máy lỗi & lập lịch giao bù cho Manager.</summary>
public sealed record SupplementaryDeliveryOverviewResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyName,
    string? DeliveryAddress,
    DateOnly StartDate,
    DateOnly EndDate,
    int TotalQuantity,
    int DeliveredGoodQuantity,
    int MissingQuantity,
    bool IsWaitingDefectiveReturn,
    string? PendingDefectiveReturnCode,
    Guid? ExistingSupplementaryHandoverId,
    string? ExistingSupplementaryHandoverCode,
    string? ExistingSupplementaryStatus,
    bool IsAlreadyScheduled,
    bool CanScheduleNow);


/// <summary>Đề xuất khoản phạt / bồi thường sự cố từ KTV chờ Manager xét duyệt.</summary>
public sealed record PendingIncidentChargeResponse(
    Guid ChargeId,
    Guid IncidentId,
    string IncidentCode,
    Guid IncidentItemId,
    Guid EquipmentId,
    string SerialNumber,
    string ModelCode,
    string ModelName,
    Guid RentalContractId,
    string ContractCode,
    string CompanyName,
    string ChargeType, // REPAIR | REPLACEMENT | OTHER
    string? Description,
    decimal ProposedAmount,
    decimal AvailableDeposit,
    string? ProposedByName,
    string? IncidentAssessment,
    List<string> EvidenceUrls,
    DateTimeOffset CreatedAt);

public sealed record DecideIncidentChargeRequest(
    /// <summary>APPROVE_OFFSET_DEPOSIT | APPROVE_DIRECT_BILLING | WAIVE</summary>
    [Required] string Decision,
    decimal? AdjustedAmount,
    string? ManagerNote);

public sealed record DecideIncidentChargeResponse(
    Guid ChargeId,
    string Status,
    string SettlementMethod,
    decimal FinalAmount,
    decimal DepositOffsetAmount,
    decimal CustomerRemainingAmount,
    bool ReplacementUnlocked,
    string Message);


// --- CHUYẾN THU HỒI THIẾT BỊ CUỐI KỲ / HẾT HẠN / HỦY HỢP ĐỒNG (RETRIEVAL) ---
public sealed record PendingRetrievalContractResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyName,
    string ContractStatus,
    DateOnly StartDate,
    DateOnly EndDate,
    string? PickupAddress,
    int OutstandingEquipmentCount,
    int DaysUntilEndDate, // < 0 là đã trễ hạn
    bool IsOverdue,
    bool IsEndingSoon,
    bool IsCancelledContract,
    bool HasActiveRetrievalTrip);

public sealed record PreviewRetrievalTripRequest(
    [Required, MinLength(1)] List<Guid> ContractIds,
    DateTimeOffset ScheduledStartAt);

public sealed record CreateRetrievalTripRequest(
    [Required, MinLength(1)] List<Guid> ContractIds,
    DateTimeOffset ScheduledStartAt,
    [Required] Guid LeaderStaffId,
    List<Guid> MemberStaffIds,
    string? Title,
    string? Note);
