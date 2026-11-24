using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Customer.DTOs;

/// <summary>Máy công ty đang thuê (đã giao, chưa trả) để chọn khi báo sự cố.</summary>
public sealed record CustomerRentedEquipmentResponse(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    Guid ContractId,
    string ContractCode
);

public sealed record CustomerIncidentResponse(
    Guid Id,
    string IncidentCode,
    string Description,
    // OPEN, REMOTE_SUPPORT, DISPATCHING, ON_SITE, RETURN_REQUIRED, RESOLVED, CANCELLED
    string Status,
    DateTimeOffset ReportedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution,
    string? ContactName,
    string? ContactPhone,
    string? ReportedByName,
    int ItemCount
);

public sealed record CustomerIncidentDetailResponse(
    CustomerIncidentResponse Incident,
    List<CustomerIncidentItemResponse> Items,
    List<CustomerIncidentAttachmentResponse> Attachments
);

public sealed record CustomerIncidentItemResponse(
    Guid EquipmentId,
    string SerialNumber,
    string ModelName,
    string? Description,
    // Đánh giá của kỹ thuật viên (nếu có).
    string? Assessment
);

public sealed record CustomerIncidentAttachmentResponse(Guid StorageFileId, string Url);

/// <summary>Khách báo sự cố: mô tả, người liên hệ, máy gặp sự cố, ảnh (tùy chọn).</summary>
public sealed class CreateCustomerIncidentRequest
{
    [Required(ErrorMessage = "Vui lòng mô tả sự cố.")]
    [MaxLength(2000, ErrorMessage = "Mô tả tối đa 2000 ký tự.")]
    public string Description { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập người liên hệ.")]
    [MaxLength(150, ErrorMessage = "Tên người liên hệ tối đa 150 ký tự.")]
    public string ContactName { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại liên hệ.")]
    [RegularExpression("^[0-9+ ]{8,20}$", ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string ContactPhone { get; init; } = string.Empty;

    [Required, MinLength(1, ErrorMessage = "Vui lòng chọn máy gặp sự cố.")]
    public List<CustomerIncidentItemRequest> Items { get; init; } = [];

    // storage_files đã upload qua upload-ticket + confirm-upload của sự cố.
    [MaxLength(5, ErrorMessage = "Tối đa 5 ảnh.")]
    public List<Guid> AttachmentFileIds { get; init; } = [];
}

public sealed class CustomerIncidentItemRequest
{
    [Required]
    public Guid? EquipmentId { get; init; }

    [MaxLength(500)]
    public string? Description { get; init; }
}
