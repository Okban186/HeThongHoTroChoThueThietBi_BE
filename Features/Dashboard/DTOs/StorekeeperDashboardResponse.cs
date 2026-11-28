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
    List<StorekeeperRecentEquipmentItem> RecentEquipments,
    int UnclaimedHandoversCount = 0,
    int MyPreparingHandoversCount = 0,
    int MyPendingApprovalHandoversCount = 0,
    int UnclaimedReturnsCount = 0,
    int MyProcessingReturnsCount = 0,
    int PendingReplacementsCount = 0,
    int ApprovedReplacementsCount = 0,
    int RejectedReplacementsCount = 0,
    int WaitingDefectiveReturnContractsCount = 0,
    int ReadySupplementaryContractsCount = 0);

public sealed record StorekeeperRecentEquipmentItem(
    Guid Id,
    string SerialNumber,
    string EquipmentModelName,
    string WarehouseName,
    string Status,
    DateTimeOffset CreatedAt);
