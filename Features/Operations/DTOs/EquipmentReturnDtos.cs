using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

public sealed record ReturnContractResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyCode,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    string? DeliveryAddress,
    int DeliveredQuantity,
    int OutstandingQuantity
);

public sealed record ReturnContractDetailResponse(
    ReturnContractResponse Contract,
    List<OutstandingEquipmentResponse> Outstanding,
    List<EquipmentReturnResponse> Returns
);

public sealed record OutstandingEquipmentResponse(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
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
    string Status,
    string ReturnType,
    string? Note,
    int ReturnedCount,
    int MissingCount,
    int DamagedCount,
    DateTimeOffset CreatedAt,
    Guid? AssignedStorekeeperId = null,
    string? AssignedStorekeeperName = null
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
    string Status,
    string Condition,
    string EquipmentStatus,
    string? Note
);

public sealed class CreateEquipmentReturnRequest
{
    [Required(ErrorMessage = "Vui lòng chọn hợp đồng.")]
    public Guid? RentalContractId { get; init; }

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

    [Required(ErrorMessage = "Vui lòng chọn kết quả kiểm máy.")]
    public string Status { get; init; } = string.Empty;

    public string? Condition { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class ConfirmProcessingReturnRequest
{
    [MaxLength(1000)]
    public string? Note { get; init; }

    [Required, MinLength(1, ErrorMessage = "Phải kiểm đếm đầy đủ toàn bộ thiết bị trong phiếu thu hồi.")]
    public List<EquipmentReturnItemRequest> Items { get; init; } = [];
}
