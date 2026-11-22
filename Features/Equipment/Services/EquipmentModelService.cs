using System.Text.Json;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Entities;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Services;

/// <summary>
/// Truy vấn model thiết bị (chỉ đọc) kèm số lượng khả dụng.
/// Các thao tác tạo/sửa model do nghiệp vụ kho bổ sung sau.
/// </summary>
public sealed class EquipmentModelService(
    AppDbContext db,
    EquipmentAvailabilityService availabilityService)
{
    public async Task<List<EquipmentModelResponse>> GetActiveModelsAsync(
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query = db.EquipmentModels
            .AsNoTracking()
            .Where(m => m.Status == EquipmentModelStatus.Active);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";

            query = query.Where(m =>
                EF.Functions.ILike(m.Code, pattern) ||
                EF.Functions.ILike(m.Name, pattern) ||
                (m.Manufacturer != null && EF.Functions.ILike(m.Manufacturer, pattern)));
        }

        var models = await query
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken);

        var today = EquipmentAvailabilityService.BusinessToday();

        var availability = await availabilityService.GetAvailabilityAsync(
            models.Select(m => m.Id).ToList(),
            today,
            today,
            cancellationToken: cancellationToken);

        return models
            .Select(m => ToResponse(m, availability[m.Id]))
            .ToList();
    }

    public async Task<EquipmentModelResponse> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var model = await FindModelAsync(id, cancellationToken);

        var today = EquipmentAvailabilityService.BusinessToday();

        var availability = await availabilityService.GetAvailabilityAsync(
            id,
            today,
            today,
            cancellationToken: cancellationToken);

        return ToResponse(model, availability);
    }

    public async Task<EquipmentAvailabilityResponse> GetAvailabilityAsync(
        Guid id,
        DateOnly from,
        DateOnly to,
        Guid? excludeReservationId,
        CancellationToken cancellationToken = default)
    {
        await FindModelAsync(id, cancellationToken);

        var result = await availabilityService.GetAvailabilityAsync(
            id,
            from,
            to,
            excludeReservationId,
            cancellationToken: cancellationToken);

        return ToAvailabilityResponse(result, from, to);
    }

    public async Task<List<EquipmentAvailabilityResponse>> CheckAvailabilityAsync(
        IReadOnlyCollection<Guid> equipmentModelIds,
        DateOnly from,
        DateOnly to,
        Guid? excludeReservationId,
        CancellationToken cancellationToken = default)
    {
        var ids = equipmentModelIds.Distinct().ToList();

        var existingCount = await db.EquipmentModels
            .AsNoTracking()
            .CountAsync(m => ids.Contains(m.Id), cancellationToken);

        if (existingCount != ids.Count)
        {
            throw new AppException(
                "EQUIPMENT_MODEL_NOT_FOUND",
                "Có model thiết bị không tồn tại.",
                StatusCodes.Status404NotFound,
                "equipmentModelIds");
        }

        var results = await availabilityService.GetAvailabilityAsync(
            ids,
            from,
            to,
            excludeReservationId,
            cancellationToken: cancellationToken);

        return ids
            .Select(id => ToAvailabilityResponse(results[id], from, to))
            .ToList();
    }

    private async Task<EquipmentModel> FindModelAsync(Guid id, CancellationToken cancellationToken)
    {
        return await db.EquipmentModels
                   .AsNoTracking()
                   .SingleOrDefaultAsync(m => m.Id == id, cancellationToken)
               ?? throw new AppException(
                   "EQUIPMENT_MODEL_NOT_FOUND",
                   "Không tìm thấy model thiết bị.",
                   StatusCodes.Status404NotFound);
    }

    private static EquipmentModelResponse ToResponse(
        EquipmentModel model,
        EquipmentAvailabilityResult availability)
    {
        return new EquipmentModelResponse(
            model.Id,
            model.Code,
            model.Name,
            model.Manufacturer,
            model.ModelNumber,
            model.Description,
            model.Unit,
            model.RentalPriceDaily,
            model.RentalPriceMonthly,
            ParseSpecifications(model.Specifications),
            EnumName.ToDatabaseValue(model.Status),
            availability.OperationalUnits,
            availability.AvailableQuantity);
    }

    private static EquipmentAvailabilityResponse ToAvailabilityResponse(
        EquipmentAvailabilityResult result,
        DateOnly from,
        DateOnly to)
    {
        return new EquipmentAvailabilityResponse(
            result.EquipmentModelId,
            from,
            to,
            result.OperationalUnits,
            result.PeakHeldQuantity,
            result.AvailableQuantity);
    }

    private static JsonElement? ParseSpecifications(string? specifications)
    {
        if (string.IsNullOrWhiteSpace(specifications))
            return null;

        using var document = JsonDocument.Parse(specifications);

        return document.RootElement.Clone();
    }
}
