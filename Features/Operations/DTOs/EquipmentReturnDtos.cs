using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

/// <summary>Hợp đồng đang có máy ở chỗ khách (đã giao, chưa nhận lại).</summary>
public sealed record ReturnContractResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyCode,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    string? DeliveryAddress,
    // Số máy đã giao cho khách.
    int DeliveredQuantity,
    // Số máy còn ở chỗ khách, chưa nhận lại.
    int OutstandingQuantity
);

public sealed record ReturnContractDetailResponse(
    ReturnContractResponse Contract,
    List<OutstandingEquipmentResponse> Outstanding,
    List<EquipmentReturnResponse> Returns
);

/// <summary>Máy đã giao, đang ở chỗ khách.</summary>
public sealed record OutstandingEquipmentResponse(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    // Tình trạng lúc giao (GOOD, FAIR).
    string Condition,
    string HandoverCode
);

public sealed record EquipmentReturnResponse(
    Guid Id,
    string ReturnCode,
    Guid RentalContractId,
    string ContractCode,
    string CompanyName,
    DateTimeOffset ReturnedAt,
    string? ReceivedByName,
    // COMPLETED, CANCELLED
    string Status,
    string? Note,
    int ReturnedCount,
    int MissingCount,
    int DamagedCount,
    DateTimeOffset CreatedAt
);

public sealed record EquipmentReturnDetailResponse(
    EquipmentReturnResponse Return,
    List<EquipmentReturnItemResponse> Items
);

public sealed record EquipmentReturnItemResponse(
    Guid Id,
    Guid EquipmentId,
    string SerialNumber,
    string ModelCode,
    string ModelName,
    // RETURNED, MISSING
    string Status,
    // Tình trạng hiện tại của máy sau khi nhận (GOOD, FAIR, DAMAGED).
    string Condition,
    // Trạng thái máy sau khi nhận (AVAILABLE, DAMAGED, LOST).
    string EquipmentStatus,
    string? Note
);

/// <summary>
/// Thủ kho lập phiếu nhận trả sau khi kỹ thuật viên thu hồi máy về kho:
/// kiểm từng serial (đã nhận / thất lạc) và tình trạng.
/// </summary>
public sealed class CreateEquipmentReturnRequest
{
    [Required(ErrorMessage = "Vui lòng chọn hợp đồng.")]
    public Guid? RentalContractId { get; init; }

    // Thời điểm máy về kho; bỏ trống = lúc lập phiếu.
    public DateTimeOffset? ReturnedAt { get; init; }

    [MaxLength(1000, ErrorMessage = "Ghi chú tối đa 1000 ký tự.")]
    public string? Note { get; init; }

    [Required, MinLength(1, ErrorMessage = "Phiếu nhận trả phải có ít nhất một máy.")]
    public List<EquipmentReturnItemRequest> Items { get; init; } = [];
}

public sealed class EquipmentReturnItemRequest
{
    [Required]
    public Guid? EquipmentId { get; init; }

    // RETURNED hoặc MISSING.
    [Required(ErrorMessage = "Vui lòng chọn kết quả kiểm máy.")]
    public string Status { get; init; } = string.Empty;

    // Với máy đã nhận: GOOD, FAIR hoặc DAMAGED.
    public string? Condition { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }
}
