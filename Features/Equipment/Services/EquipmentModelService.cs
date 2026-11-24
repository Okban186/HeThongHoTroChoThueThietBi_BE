using System.Text.Json;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Entities;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Services;

/// <summary>
/// Truy vấn model thiết bị (chỉ đọc) kèm số lượng khả dụng, danh mục và ảnh.
/// Các thao tác tạo/sửa model nằm ở <see cref="StorekeeperEquipmentService"/>.
/// </summary>
public sealed class EquipmentModelService(
    AppDbContext db,
    EquipmentAvailabilityService availabilityService,
    IFileUploadService fileUploadService)
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

        return await ToResponsesAsync(models, cancellationToken);
    }

    public async Task<EquipmentModelResponse> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var model = await FindModelAsync(id, cancellationToken);

        return (await ToResponsesAsync([model], cancellationToken)).Single();
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

    /// <summary>Ghép số lượng khả dụng hôm nay, tổng số máy, danh mục và ảnh cho danh sách model.</summary>
    private async Task<List<EquipmentModelResponse>> ToResponsesAsync(
        List<EquipmentModel> models,
        CancellationToken cancellationToken)
    {
        if (models.Count == 0)
            return [];

        var modelIds = models.Select(m => m.Id).ToList();
        var today = EquipmentAvailabilityService.BusinessToday();

        var availability = await availabilityService.GetAvailabilityAsync(
            modelIds,
            today,
            today,
            cancellationToken: cancellationToken);

        var totalUnits = await db.Equipments
            .AsNoTracking()
            .Where(e => modelIds.Contains(e.EquipmentModelId))
            .GroupBy(e => e.EquipmentModelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var categories = (await (
                from link in db.EquipmentModelCategories.AsNoTracking()
                join category in db.Categories.AsNoTracking() on link.CategoryId equals category.Id
                where modelIds.Contains(link.EquipmentModelId)
                orderby category.Name
                select new { link.EquipmentModelId, category }
            ).ToListAsync(cancellationToken))
            .ToLookup(
                x => x.EquipmentModelId,
                x => new CategoryResponse(
                    x.category.Id,
                    x.category.ParentId,
                    x.category.Code,
                    x.category.Name,
                    x.category.Description,
                    EnumName.ToDatabaseValue(x.category.Status),
                    0));

        var media = (await (
                from item in db.EquipmentModelMedia.AsNoTracking()
                join file in db.StorageFiles.AsNoTracking() on item.StorageFileId equals file.Id
                where modelIds.Contains(item.EquipmentModelId)
                orderby item.IsPrimary descending, item.DisplayOrder
                select new { item, file }
            ).ToListAsync(cancellationToken))
            .ToLookup(
                x => x.item.EquipmentModelId,
                x => new EquipmentModelMediaResponse(
                    x.item.Id,
                    x.item.StorageFileId,
                    fileUploadService.GetPrivateImageUrl(
                        $"{x.file.BucketName}/{x.file.ObjectKey}",
                        x.file.ResourceType.ToCloudinaryNetResourceType(),
                        x.file.ContentType!),
                    x.item.MediaType,
                    x.item.DisplayOrder,
                    x.item.IsPrimary));

        return models
            .Select(model => new EquipmentModelResponse(
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
                availability[model.Id].OperationalUnits,
                availability[model.Id].AvailableQuantity,
                totalUnits.GetValueOrDefault(model.Id),
                categories[model.Id].ToList(),
                media[model.Id].ToList()))
            .ToList();
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
