using System.Text.Json;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;

public sealed record EquipmentModelResponse(
    Guid Id,
    string Code,
    string Name,
    string? Manufacturer,
    string? ModelNumber,
    string? Description,
    string Unit,
    decimal RentalPriceDaily,
    decimal RentalPriceMonthly,
    JsonElement? Specifications,
    string Status,
    // Số máy hoạt động (AVAILABLE, RESERVED, RENTED).
    int OperationalUnits,
    // Số lượng khả dụng trong ngày hôm nay.
    int AvailableToday
);
