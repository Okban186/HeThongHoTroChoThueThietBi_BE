using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Operations.DTOs;

public sealed record HandoverContractResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyCode,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    string ContractStatus,
    string? DeliveryAddress,
    int TotalQuantity,
    int AllocatedQuantity,
    int ActiveNoteCount,
    int PendingDefectiveReturnCount = 0,
    string? PendingDefectiveReturnCode = null,
    Guid? ReadyIncidentReplacementId = null,
    string? ReadyIncidentCode = null,
    string? BlockedIncidentReplacementReason = null
);

public sealed record HandoverContractDetailResponse(
    HandoverContractResponse Contract,
    string? DeliveryNote,
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
    int AvailableInStock
);

public sealed record AvailableEquipmentResponse(
    Guid Id,
    string SerialNumber,
    Guid EquipmentModelId,
    Guid WarehouseId,
    string WarehouseName,
    string Condition
);

public sealed record LookupSerialResponse(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    Guid WarehouseId,
    string WarehouseName,
    string Condition,
    string Status,
    bool IsAvailableForHandover,
    bool IsContractModel,
    string? UnavailableReason
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
    string Status,
    int ItemCount,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string HandoverType = "INITIAL",
    Guid? ParentHandoverId = null,
    Guid? AssignedStorekeeperId = null,
    string? AssignedStorekeeperName = null,
    Guid? IncidentId = null
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
    string EquipmentStatus,
    string? Note,
    Guid? ReplacedForModelId = null,
    string? ReplacedForModelCode = null,
    string? ReplacedForModelName = null
);

public sealed class CreateHandoverRequest : SaveHandoverRequest
{
    [Required(ErrorMessage = "Vui lòng chọn hợp đồng.")]
    public Guid? RentalContractId { get; init; }

    /// <summary>Truyền IncidentId khi lập phiếu bàn giao máy thay thế sự cố (INCIDENT_REPLACEMENT).</summary>
    public Guid? IncidentId { get; init; }
}

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

    public Guid? ReplacedForModelId { get; init; }
}

public sealed class CancelHandoverRequest
{
    [Required(ErrorMessage = "Vui lòng nhập lý do hủy.")]
    [MaxLength(500)]
    public string Reason { get; init; } = string.Empty;
}


public sealed class CompleteHandoverDeliveryRequest
{
    [MaxLength(1000)]
    public string? Note { get; init; }

    /// <summary>Danh sách máy bị lỗi ngay lúc giao/lắp đặt cần mang về kho bảo dưỡng và giao bù.</summary>
    public List<DefectiveDeliveryItemRequest> DefectiveItems { get; init; } = [];
}

public sealed class DefectiveDeliveryItemRequest
{
    [Required]
    public Guid? EquipmentId { get; init; }

    /// <summary>VD: DAMAGED_ON_DELIVERY, MISSING_ACCESSORY, TECHNICAL_FAULT.</summary>
    [MaxLength(30)]
    public string IssueType { get; init; } = "DAMAGED_ON_DELIVERY";

    [MaxLength(500)]
    public string? Description { get; init; }
}
