using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

/// <summary>Hợp đồng cần kho soạn bàn giao (đã ký, chưa giao đủ máy).</summary>
public sealed record HandoverContractResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyCode,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    // SIGNED, PREPARING, PARTIALLY_DELIVERED
    string ContractStatus,
    string? DeliveryAddress,
    // Tổng số máy theo hợp đồng.
    int TotalQuantity,
    // Số máy đã được xếp vào phiếu bàn giao (trừ phiếu đã hủy).
    int AllocatedQuantity,
    int ActiveNoteCount
);

/// <summary>Chi tiết chuẩn bị của một hợp đồng: số lượng từng model, các phiếu đã soạn.</summary>
public sealed record HandoverContractDetailResponse(
    HandoverContractResponse Contract,
    string? DeliveryNote,
    // Người đại diện ký hợp đồng, dùng làm người nhận mặc định.
    string? RepresentativeName,
    string? RepresentativePhone,
    List<HandoverContractItemResponse> Items,
    List<HandoverNoteResponse> Notes
);

public sealed record HandoverContractItemResponse(
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    string Unit,
    int Quantity,
    int AllocatedQuantity,
    // Số máy trong kho đang sẵn sàng (AVAILABLE, không hỏng) để chọn.
    int AvailableInStock
);

/// <summary>Máy sẵn sàng trong kho để chọn vào phiếu bàn giao.</summary>
public sealed record AvailableEquipmentResponse(
    Guid Id,
    string SerialNumber,
    Guid EquipmentModelId,
    Guid WarehouseId,
    string WarehouseName,
    // GOOD, FAIR
    string Condition
);

public sealed record HandoverNoteResponse(
    Guid Id,
    string HandoverCode,
    Guid RentalContractId,
    string ContractCode,
    string CompanyName,
    DateOnly HandoverDate,
    string ReceivedByName,
    string? ReceivedByPhone,
    string? Note,
    // PREPARING, PENDING_APPROVAL, SHIPPING, COMPLETED, CANCELLED
    string Status,
    int ItemCount,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record HandoverNoteDetailResponse(
    HandoverNoteResponse Handover,
    string? DeliveryAddress,
    string? DeliveryNote,
    List<HandoverItemResponse> Items
);

public sealed record HandoverItemResponse(
    Guid Id,
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    string WarehouseName,
    string Condition,
    // Trạng thái hiện tại của máy (RESERVED khi đang soạn, RENTED khi đã giao).
    string EquipmentStatus,
    string? Note
);

/// <summary>Tạo phiếu bàn giao (thủ kho chọn máy cụ thể theo serial).</summary>
public sealed class CreateHandoverRequest : SaveHandoverRequest
{
    [Required(ErrorMessage = "Vui lòng chọn hợp đồng.")]
    public Guid? RentalContractId { get; init; }
}

/// <summary>Sửa phiếu khi còn đang soạn (PREPARING).</summary>
public class SaveHandoverRequest
{
    [Required(ErrorMessage = "Vui lòng chọn ngày giao.")]
    public DateOnly? HandoverDate { get; init; }

    [Required(ErrorMessage = "Vui lòng nhập người nhận.")]
    [MaxLength(150, ErrorMessage = "Tên người nhận tối đa 150 ký tự.")]
    public string ReceivedByName { get; init; } = string.Empty;

    [RegularExpression("^[0-9+ ]{8,20}$", ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string? ReceivedByPhone { get; init; }

    [MaxLength(1000, ErrorMessage = "Ghi chú tối đa 1000 ký tự.")]
    public string? Note { get; init; }

    [Required, MinLength(1, ErrorMessage = "Phiếu bàn giao phải có ít nhất một máy.")]
    public List<HandoverItemRequest> Items { get; init; } = [];
}

public sealed class HandoverItemRequest
{
    [Required]
    public Guid? EquipmentId { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class CancelHandoverRequest
{
    [Required(ErrorMessage = "Vui lòng nhập lý do hủy.")]
    [MaxLength(500)]
    public string Reason { get; init; } = string.Empty;
}
