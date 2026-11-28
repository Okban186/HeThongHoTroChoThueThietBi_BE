using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;

// --- WAREHOUSE ---
public sealed record WarehouseResponse(
    Guid Id, string Code, string Name, string? Address, string? Phone, string Status, int EquipmentCount);

public sealed record SaveWarehouseRequest(
    [Required, MaxLength(50)] string Code,
    [Required, MaxLength(150)] string Name,
    string? Address,
    [MaxLength(20)] string? Phone);

// --- CATEGORY ---
public sealed record CategoryResponse(
    Guid Id, Guid? ParentId, string Code, string Name, string? Description, string Status, int ModelCount);

public sealed record SaveCategoryRequest(
    [Required, MaxLength(150)] string Name,
    string? Description,
    string? Status = null);

public sealed record UpdateEntityStatusRequest([Required] string Status);

// --- EQUIPMENT MODEL ---
public sealed record EquipmentModelMediaResponse(
    Guid Id, Guid StorageFileId, string Url, string MediaType, int DisplayOrder, bool IsPrimary);

public sealed record EquipmentModelDetailResponse(
    Guid Id,
    string Code,
    string Name,
    string? Manufacturer,
    string? ModelNumber,
    string? Description,
    int EstimatedInstallationMinutes,
    string Unit,
    decimal RentalPriceDaily,
    decimal RentalPriceMonthly,
    JsonElement? Specifications,
    string Status,
    int TotalUnits,
    int OperationalUnits,
    int AvailableToday,
    List<CategoryResponse> Categories,
    List<EquipmentModelMediaResponse> Media);

public sealed record SaveEquipmentModelRequest(
    [Required, MaxLength(50)] string Code,
    [Required, MaxLength(255)] string Name,
    [MaxLength(150)] string? Manufacturer,
    [MaxLength(100)] string? ModelNumber,
    string? Description,
    [Range(0, 100000)] int EstimatedInstallationMinutes,
    [Required, MaxLength(30)] string Unit,
    [Range(0, double.MaxValue)] decimal RentalPriceDaily,
    [Range(0, double.MaxValue)] decimal RentalPriceMonthly,
    JsonElement? Specifications,
    List<Guid>? CategoryIds);

public sealed record AttachEquipmentModelMediaRequest(
    [Required] Guid StorageFileId,
    string MediaType = "IMAGE",
    bool IsPrimary = false);

// --- EQUIPMENT (VẬT LÝ) ---
public sealed record EquipmentItemResponse(
    Guid Id,
    Guid EquipmentModelId,
    string EquipmentModelCode,
    string EquipmentModelName,
    Guid WarehouseId,
    string WarehouseName,
    string SerialNumber,
    string Status,
    string Condition,
    DateOnly? AcquiredAt,
    decimal? AcquisitionCost,
    string? Note,
    DateTimeOffset CreatedAt);

public sealed record CreateEquipmentRequest(
    [Required] Guid EquipmentModelId,
    [Required] Guid WarehouseId,
    [Required, MaxLength(100)] string SerialNumber,
    string Condition = "GOOD",
    DateOnly? AcquiredAt = null,
    [Range(0, double.MaxValue)] decimal? AcquisitionCost = null,
    string? Note = null);

public sealed record BatchCreateEquipmentRequest(
    [Required] Guid EquipmentModelId,
    [Required] Guid WarehouseId,
    [Required, MinLength(1)] List<string> SerialNumbers,
    string Condition = "GOOD",
    DateOnly? AcquiredAt = null,
    [Range(0, double.MaxValue)] decimal? AcquisitionCost = null,
    string? Note = null);

public sealed record UpdateEquipmentRequest(
    [Required] Guid WarehouseId,
    [Required] string Status,
    [Required] string Condition,
    DateOnly? AcquiredAt,
    [Range(0, double.MaxValue)] decimal? AcquisitionCost,
    string? Note);

public sealed record PagedResponse<T>(
    List<T> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    public bool HasMore => Page * PageSize < TotalCount;
}


public sealed record NextSerialPreviewResponse(
    string Prefix,
    int NextSequence,
    int PadLength);

public sealed record AutoGenerateEquipmentsRequest(
    [Required] Guid EquipmentModelId,
    Guid? WarehouseId,
    [Range(1, 200)] int Quantity,
    DateOnly? AcquiredAt = null,
    [Range(0, double.MaxValue)] decimal? AcquisitionCost = null,
    string? Note = null);

// --- IMPORT EXCEL EQUIPMENT MODEL ---
public sealed record ImportEquipmentModelRowError(
    int RowNumber,
    string? Code,
    string? Name,
    string Message);

public sealed record ImportEquipmentModelsResult(
    int TotalRows,
    int CreatedCount,
    int FailedCount,
    List<ImportEquipmentModelRowError> Errors,
    List<EquipmentModelDetailResponse> CreatedModels);


// --- LỊCH SỬ VÒNG ĐỜI & SỬA CHỮA / LINH KIỆN THIẾT BỊ VẬT LÝ ---
public sealed record EquipmentLifecycleHistoryResponse(
    Guid EquipmentId,
    string SerialNumber,
    Guid EquipmentModelId,
    string ModelCode,
    string ModelName,
    Guid WarehouseId,
    string WarehouseName,
    string Status,
    string Condition,
    DateOnly? AcquiredAt,
    decimal? AcquisitionCost,
    string? Note,
    int TotalRentalsCount,
    int TotalMaintenanceCount,
    int TotalIncidentsCount,
    decimal TotalLaborCost,
    decimal TotalPartsCost,
    decimal TotalMaintenanceCost,
    double? RepairToAcquisitionPercent,
    List<EquipmentMaintenanceHistoryDto> MaintenanceHistory,
    List<EquipmentIncidentHistoryDto> IncidentHistory,
    List<EquipmentRentalHistoryDto> RentalHistory);

public sealed record EquipmentMaintenanceHistoryDto(
    Guid TaskId,
    string Type,
    string Status,
    string Description,
    string? Result,
    string? AssignedTechnicianName,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    decimal TotalLaborCost,
    decimal TotalPartsCost,
    List<string> AttachmentUrls,
    List<EquipmentMaintenanceRecordHistoryDto> Records);

public sealed record EquipmentMaintenanceRecordHistoryDto(
    Guid RecordId,
    string PerformedByName,
    string Description,
    decimal LaborCost,
    decimal PartsTotalCost,
    DateTimeOffset? CompletedAt,
    string? Note,
    List<EquipmentMaintenancePartHistoryDto> Parts);

public sealed record EquipmentMaintenancePartHistoryDto(
    Guid PartId,
    string Name,
    decimal Quantity,
    decimal UnitCost,
    decimal TotalCost,
    string? Note);

public sealed record EquipmentIncidentHistoryDto(
    Guid IncidentId,
    string IncidentCode,
    string CompanyName,
    string Status,
    DateTimeOffset ReportedAt,
    DateTimeOffset? ResolvedAt,
    string Description,
    string? FaultType,
    string? Assessment,
    string? Resolution,
    decimal? ChargeAmount,
    string? ChargeStatus);

public sealed record EquipmentRentalHistoryDto(
    Guid RentalContractId,
    string ContractCode,
    string CompanyName,
    string ContractStatus,
    DateOnly StartDate,
    DateOnly EndDate,
    string HandoverCode,
    string HandoverType,
    DateOnly HandoverDate,
    string HandoverStatus);
