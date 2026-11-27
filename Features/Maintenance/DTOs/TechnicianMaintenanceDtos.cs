using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.DTOs;

public sealed record MaintenanceTaskSummaryResponse(
    Guid Id,
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    string WarehouseName,
    string EquipmentStatus,
    string EquipmentCondition,
    string Type, // INSPECTION | PREVENTIVE | CORRECTIVE
    string Description,
    string Status, // PENDING | IN_PROGRESS | COMPLETED | CANCELLED
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Result,
    int AttachmentCount,
    int RecordCount,
    DateTimeOffset CreatedAt,
    Guid? AssignedTechnicianStaffId = null,
    string? AssignedTechnicianName = null,
    bool IsAssignedToMe = false
);

public sealed record MaintenancePartResponse(
    Guid Id,
    string Name,
    decimal Quantity,
    decimal UnitCost,
    decimal TotalCost,
    string? Note
);

public sealed record MaintenanceRecordResponse(
    Guid Id,
    Guid PerformedByStaffId,
    string PerformedByName,
    string Description,
    decimal LaborCost,
    decimal PartsTotalCost,
    DateTimeOffset? CompletedAt,
    string? Note,
    List<MaintenancePartResponse> Parts
);

public sealed record MaintenanceIncidentContextDto(
    Guid IncidentId,
    string IncidentCode,
    Guid IncidentItemId,
    Guid? RentalContractId,
    string? ContractCode,
    string CompanyName,
    string IncidentDescription,
    string? ExistingFaultType,
    string? ExistingChargeStatus,
    decimal? ExistingChargeAmount
);

public sealed record MaintenanceTaskDetailResponse(
    MaintenanceTaskSummaryResponse Task,
    List<string> AttachmentUrls,
    List<MaintenanceRecordResponse> Records,
    MaintenanceIncidentContextDto? IncidentContext = null
);

public sealed class CreateMaintenanceTaskRequest
{
    [Required(ErrorMessage = "Vui lòng chọn thiết bị cần bảo trì.")]
    public Guid? EquipmentId { get; init; }

    /// <summary>INSPECTION | PREVENTIVE | CORRECTIVE</summary>
    [MaxLength(20)]
    public string Type { get; init; } = "INSPECTION";

    [Required(ErrorMessage = "Vui lòng nhập mô tả công việc bảo trì.")]
    [MaxLength(1500)]
    public string Description { get; init; } = string.Empty;

    public DateTimeOffset? ScheduledAt { get; init; }
}

public sealed class MaintenancePartInputRequest
{
    [Required(ErrorMessage = "Vui lòng nhập tên linh kiện / vật tư.")]
    [MaxLength(255)]
    public string Name { get; init; } = string.Empty;

    [Range(0.01, 999999, ErrorMessage = "Số lượng linh kiện phải lớn hơn 0.")]
    public decimal Quantity { get; init; } = 1;

    [Range(0, 999_999_999_999, ErrorMessage = "Đơn giá linh kiện không hợp lệ.")]
    public decimal UnitCost { get; init; } = 0;

    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class CompleteMaintenanceTaskRequest
{
    [Required(ErrorMessage = "Vui lòng nhập nội dung kiểm tra / sửa chữa đã thực hiện.")]
    [MaxLength(2000)]
    public string Description { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập kết luận sau bảo trì.")]
    [MaxLength(1000)]
    public string Result { get; init; } = string.Empty;

    /// <summary>
    /// AVAILABLE: Đạt, đưa máy về kho sẵn sàng cho thuê.
    /// DAMAGED: Hỏng nặng / không thể khắc phục, chuyển sang DAMAGED để kho/quản lý xử lý thanh lý.
    /// KEEP_MAINTENANCE: Chưa hoàn tất hẳn (chờ linh kiện/sửa chuyên sâu), giữ máy ở MAINTENANCE và mở phiếu CORRECTIVE tiếp theo.
    /// </summary>
    [Required(ErrorMessage = "Vui lòng chọn kết quả đầu ra của thiết bị.")]
    [MaxLength(30)]
    public string Outcome { get; init; } = "AVAILABLE";

    /// <summary>GOOD | FAIR | DAMAGED</summary>
    [MaxLength(20)]
    public string? NewCondition { get; init; }

    [Range(0, 999_999_999_999, ErrorMessage = "Chi phí nhân công không hợp lệ.")]
    public decimal LaborCost { get; init; } = 0;

    [MaxLength(1000)]
    public string? Note { get; init; }

    public List<MaintenancePartInputRequest> Parts { get; init; } = [];

    /// <summary>Bắt buộc tối thiểu 1 ảnh minh chứng kiểm tra/sửa chữa thiết bị.</summary>
    [Required, MinLength(1, ErrorMessage = "Vui lòng chụp ít nhất 1 ảnh minh chứng hoàn tất bảo trì.")]
    [MaxLength(10, ErrorMessage = "Tối đa 10 ảnh minh chứng.")]
    public List<Guid> AttachmentFileIds { get; init; } = [];

    // --- Bước 1 (chỉ áp dụng khi phiếu bảo trì xuất phát từ thiết bị sự cố thu hồi RETURN_REQUIRED) ---

    /// <summary>TECHNICAL_FAULT (lỗi kỹ thuật/hao mòn tự nhiên -> 0 đ) | CUSTOMER_FAULT (lỗi do khách hàng -> đề xuất phí).</summary>
    [MaxLength(30)]
    public string? IncidentFaultType { get; init; }

    /// <summary>REPAIR (sửa chữa) | REPLACEMENT (đền bù máy hỏng hoàn toàn) | OTHER.</summary>
    [MaxLength(20)]
    public string? ProposedChargeType { get; init; }

    /// <summary>Số tiền đề xuất thu khách hàng nếu CUSTOMER_FAULT (nếu để null sẽ mặc định lấy LaborCost + tổng linh kiện).</summary>
    [Range(0, 999_999_999_999, ErrorMessage = "Số tiền đề xuất không hợp lệ.")]
    public decimal? ProposedChargeAmount { get; init; }

    [MaxLength(1000)]
    public string? ProposedChargeDescription { get; init; }
}

public sealed record CompleteMaintenanceTaskResponse(
    Guid TaskId,
    Guid EquipmentId,
    string SerialNumber,
    string EquipmentStatus,
    string EquipmentCondition,
    decimal TotalCost,
    Guid? FollowUpTaskId,
    string Message
);


public sealed class AssignMaintenanceTaskRequest
{
    [Required(ErrorMessage = "Vui lòng chọn kỹ thuật viên phụ trách.")]
    public Guid? TechnicianStaffId { get; init; }
}
