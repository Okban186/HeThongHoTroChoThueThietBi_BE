namespace HeThongHoTroChoThueThietBi.Features.Dashboard.DTOs;

public sealed record StorekeeperDashboardResponse(
    int TotalModels,
    int ActiveModels,
    int TotalEquipments,
    int AvailableEquipments,
    int RentedEquipments,
    int ReservedEquipments,
    int MaintenanceEquipments,
    int DamagedOrLostEquipments,
    int OutOfStockModelsCount,
    int ActiveWarehousesCount,
    int ActiveCategoriesCount,
    List<StorekeeperRecentEquipmentItem> RecentEquipments);

public sealed record StorekeeperRecentEquipmentItem(
    Guid Id,
    string SerialNumber,
    string EquipmentModelName,
    string WarehouseName,
    string Status,
    DateTimeOffset CreatedAt);