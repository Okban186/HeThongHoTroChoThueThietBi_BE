namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

public sealed record ReservationItemResponse(
    Guid Id,
    Guid EquipmentModelId,
    string EquipmentModelCode,
    string EquipmentModelName,
    string Unit,
    decimal RentalPriceDaily,
    decimal RentalPriceMonthly,
    int Quantity,
    string? Note,
    // Số lượng tối đa phiếu này có thể giữ cho model trong khoảng ngày của phiếu
    // (đã loại trừ chính phiếu này).
    int AvailableQuantity
);
