using System.ComponentModel.DataAnnotations;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.DTOs;

public sealed record TechnicianIncidentSummaryResponse(
    Guid IncidentId,
    string IncidentCode,
    string AssignmentMode, // REMOTE | ON_SITE
    bool IsLeader,
    Guid CompanyId,
    string CompanyName,
    string? ServiceAddress,
    string? ContactName,
    string? ContactPhone,
    string Description,
    string Status,
    DateTimeOffset ReportedAt,
    Guid? ServiceAssignmentId,
    string? AssignmentStatus,
    DateTimeOffset? ScheduledStartAt,
    int? EstimatedDurationMinutes,
    int ItemCount,
    List<string> SerialNumbers
);

public sealed record TechnicianIncidentItemDetailDto(
    Guid IncidentItemId,
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    string EquipmentStatus,
    string EquipmentCondition,
    string? CustomerDescription,
    string? Assessment,
    string? Note,
    Guid? RentalContractId,
    string? ContractCode
);

public sealed record TechnicianIncidentDetailResponse(
    Guid IncidentId,
    string IncidentCode,
    string AssignmentMode, // REMOTE | ON_SITE
    bool IsLeader,
    Guid CompanyId,
    string CompanyName,
    string? ServiceAddress,
    double? Latitude,
    double? Longitude,
    string? ContactName,
    string? ContactPhone,
    string Description,
    string Status,
    string? Resolution,
    DateTimeOffset ReportedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ServiceAssignmentId,
    string? AssignmentStatus,
    DateTimeOffset? ScheduledStartAt,
    int? EstimatedDurationMinutes,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TechnicianDepotDto? Depot,
    List<TechnicianAssignmentMemberDto> Members,
    List<TechnicianIncidentItemDetailDto> Items,
    List<string> AttachmentUrls
);

public sealed class TechnicianIncidentItemAssessmentInput
{
    [Required]
    public Guid? EquipmentId { get; init; }

    [MaxLength(1000)]
    public string? Assessment { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }

    /// <summary>Khi Outcome = RETURN_REQUIRED: đánh dấu máy này cần mang về kho bảo dưỡng.</summary>
    public bool NeedsReturn { get; init; } = true;
}

public sealed class TechnicianResolveIncidentRequest
{
    /// <summary>RESOLVED (xử lý xong) | REMOTE_FAILED (hỗ trợ Online không khắc phục được -> trả về Manager) | RETURN_REQUIRED (tại hiện trường: cần thu hồi máy về kho).</summary>
    [Required(ErrorMessage = "Vui lòng chọn kết quả xử lý sự cố.")]
    [MaxLength(30)]
    public string Outcome { get; init; } = "RESOLVED";

    [Required(ErrorMessage = "Vui lòng nhập kết quả xử lý / lý do thu hồi.")]
    [MaxLength(2000)]
    public string Resolution { get; init; } = string.Empty;

    public List<TechnicianIncidentItemAssessmentInput> ItemAssessments { get; init; } = [];

    /// <summary>Bắt buộc tối thiểu 1 ảnh minh chứng khi xử lý tại hiện trường hoặc yêu cầu thu hồi máy.</summary>
    [MaxLength(10, ErrorMessage = "Tối đa 10 ảnh minh chứng.")]
    public List<Guid> AttachmentFileIds { get; init; } = [];
}

public sealed record TechnicianResolveIncidentResponse(
    Guid IncidentId,
    string IncidentCode,
    string Status,
    List<string> AutoReturnCodes,
    string Message
);
