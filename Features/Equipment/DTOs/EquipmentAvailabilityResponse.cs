namespace HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;

public sealed record EquipmentAvailabilityResponse(
    Guid EquipmentModelId,
    DateOnly From,
    DateOnly To,
    // Số máy hoạt động (AVAILABLE, RESERVED, RENTED).
    int OperationalUnits,
    // Mức chiếm dụng cao nhất trong một ngày của khoảng [From, To].
    int PeakHeldQuantity,
    // Số lượng còn có thể giữ chỗ/cho thuê trong suốt khoảng [From, To].
    int AvailableQuantity
);
