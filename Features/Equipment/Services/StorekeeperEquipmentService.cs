using System.Text.Json;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Entities;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Services;

public sealed class StorekeeperEquipmentService(
    AppDbContext db,
    EquipmentAvailabilityService availabilityService,
    IFileUploadService fileUploadService
    )
{
    private static readonly HashSet<string> ValidConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GOOD", "FAIR", "DAMAGED"
    };

    public async Task<List<WarehouseResponse>> GetWarehousesAsync(string? keyword, bool includeInactive, CancellationToken ct)
    {
        var query = db.Warehouses.AsNoTracking();
        if (!includeInactive) query = query.Where(w => w.Status == WarehouseStatus.Active);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var p = $"%{keyword.Trim()}%";
            query = query.Where(w => EF.Functions.ILike(w.Code, p) || EF.Functions.ILike(w.Name, p));
        }

        var list = await query.OrderBy(w => w.Name).ToListAsync(ct);
        var counts = await db.Equipments.AsNoTracking()
            .GroupBy(e => e.WarehouseId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return list.Select(w => new WarehouseResponse(
            w.Id, w.Code, w.Name, w.Address, w.Phone,
            EnumName.ToDatabaseValue(w.Status),
            counts.GetValueOrDefault(w.Id))).ToList();
    }

    public async Task<WarehouseResponse> CreateWarehouseAsync(SaveWarehouseRequest req, CancellationToken ct)
    {
        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.Warehouses.AnyAsync(w => w.Code == code, ct))
            throw new AppException("WAREHOUSE_CODE_EXISTS", "Mã kho đã tồn tại.", StatusCodes.Status409Conflict, "code");

        var now = DateTimeOffset.UtcNow;
        var entity = new Warehouse
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = req.Name.Trim(),
            Address = req.Address?.Trim(),
            Phone = req.Phone?.Trim(),
            Status = WarehouseStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Warehouses.Add(entity);
        await db.SaveChangesAsync(ct);
        return new WarehouseResponse(entity.Id, entity.Code, entity.Name, entity.Address, entity.Phone, "ACTIVE", 0);
    }

    public async Task<WarehouseResponse> UpdateWarehouseAsync(Guid id, SaveWarehouseRequest req, CancellationToken ct)
    {
        var entity = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new AppException("WAREHOUSE_NOT_FOUND", "Không tìm thấy kho.", StatusCodes.Status404NotFound);

        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.Warehouses.AnyAsync(w => w.Code == code && w.Id != id, ct))
            throw new AppException("WAREHOUSE_CODE_EXISTS", "Mã kho đã tồn tại.", StatusCodes.Status409Conflict, "code");

        entity.Code = code;
        entity.Name = req.Name.Trim();
        entity.Address = req.Address?.Trim();
        entity.Phone = req.Phone?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var count = await db.Equipments.CountAsync(e => e.WarehouseId == id, ct);
        return new WarehouseResponse(entity.Id, entity.Code, entity.Name, entity.Address, entity.Phone, EnumName.ToDatabaseValue(entity.Status), count);
    }

    public async Task<List<CategoryResponse>> GetCategoriesAsync(string? keyword, bool includeInactive, CancellationToken ct)
    {
        var query = db.Categories.AsNoTracking();
        if (!includeInactive) query = query.Where(c => c.Status == CategoryStatus.Active);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var p = $"%{keyword.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Code, p) || EF.Functions.ILike(c.Name, p));
        }

        var list = await query.OrderBy(c => c.Name).ToListAsync(ct);
        var counts = await db.EquipmentModelCategories.AsNoTracking()
            .GroupBy(x => x.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return list.Select(c => new CategoryResponse(
            c.Id, c.ParentId, c.Code, c.Name, c.Description,
            EnumName.ToDatabaseValue(c.Status),
            counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<CategoryResponse> CreateCategoryAsync(SaveCategoryRequest req, CancellationToken ct)
    {
        var existingCodes = await db.Categories.AsNoTracking()
            .Where(c => c.Code.StartsWith("CAT-"))
            .Select(c => c.Code)
            .ToListAsync(ct);

        var maxNum = 0;
        foreach (var c in existingCodes)
        {
            if (int.TryParse(c["CAT-".Length..], out var n) && n > maxNum)
                maxNum = n;
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new Category
        {
            Id = Guid.CreateVersion7(),
            ParentId = null,
            Code = $"CAT-{(maxNum + 1):D3}",
            Name = req.Name.Trim(),
            Description = req.Description?.Trim(),
            Status = CategoryStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Categories.Add(entity);
        await db.SaveChangesAsync(ct);

        return new CategoryResponse(entity.Id, null, entity.Code, entity.Name, entity.Description, "ACTIVE", 0);
    }

    public async Task<CategoryResponse> UpdateCategoryAsync(Guid id, SaveCategoryRequest req, CancellationToken ct)
    {
        var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new AppException("CATEGORY_NOT_FOUND", "Không tìm thấy danh mục.", StatusCodes.Status404NotFound);

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!Enum.TryParse<CategoryStatus>(req.Status.Trim(), true, out var parsedStatus))
                throw new AppException("INVALID_STATUS", "Trạng thái phải là ACTIVE hoặc INACTIVE.", StatusCodes.Status400BadRequest, "status");

            entity.Status = parsedStatus;
        }

        entity.Name = req.Name.Trim();
        entity.Description = req.Description?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var count = await db.EquipmentModelCategories.CountAsync(x => x.CategoryId == id, ct);
        return new CategoryResponse(
            entity.Id,
            entity.ParentId,
            entity.Code,
            entity.Name,
            entity.Description,
            EnumName.ToDatabaseValue(entity.Status),
            count);
    }

    public async Task<CategoryResponse> UpdateCategoryStatusAsync(Guid id, string status, CancellationToken ct)
    {
        var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new AppException("CATEGORY_NOT_FOUND", "Không tìm thấy danh mục.", StatusCodes.Status404NotFound);

        if (!Enum.TryParse<CategoryStatus>(status.Trim(), true, out var parsed))
            throw new AppException("INVALID_STATUS", "Trạng thái phải là ACTIVE hoặc INACTIVE.", StatusCodes.Status400BadRequest, "status");

        entity.Status = parsed;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var count = await db.EquipmentModelCategories.CountAsync(x => x.CategoryId == id, ct);
        return new CategoryResponse(
            entity.Id,
            entity.ParentId,
            entity.Code,
            entity.Name,
            entity.Description,
            EnumName.ToDatabaseValue(entity.Status),
            count);
    }

    public async Task SetPrimaryMediaAsync(Guid modelId, Guid mediaId, CancellationToken ct)
    {
        var list = await db.EquipmentModelMedia
            .Where(m => m.EquipmentModelId == modelId)
            .ToListAsync(ct);

        var target = list.FirstOrDefault(m => m.Id == mediaId)
            ?? throw new AppException("MEDIA_NOT_FOUND", "Không tìm thấy ảnh.", StatusCodes.Status404NotFound);

        foreach (var item in list)
            item.IsPrimary = item.Id == target.Id;

        await db.SaveChangesAsync(ct);
    }
    public async Task<List<EquipmentModelDetailResponse>> GetModelsForStorekeeperAsync(
        string? keyword, Guid? categoryId, string? status, CancellationToken ct)
    {
        var query = db.EquipmentModels.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<EquipmentModelStatus>(status, true, out var parsedStatus))
            query = query.Where(m => m.Status == parsedStatus);

        if (categoryId.HasValue)
        {
            query = query.Where(m => db.EquipmentModelCategories
                .Any(mc => mc.EquipmentModelId == m.Id && mc.CategoryId == categoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var p = $"%{keyword.Trim()}%";
            query = query.Where(m =>
                EF.Functions.ILike(m.Code, p) ||
                EF.Functions.ILike(m.Name, p) ||
                (m.Manufacturer != null && EF.Functions.ILike(m.Manufacturer, p)));
        }

        var models = await query.OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
        return await BuildModelDetailListAsync(models, ct);
    }

    public Task<FileUploadTicket> CreateModelMediaUploadTicketAsync(
FileUploadPolicy policy,
        FileUploadRequest request,
        Guid uploadedBy,
        CancellationToken ct)
    {
        return fileUploadService.CreateUploadTicketAsync(
            policy,
            request,
            uploadedBy,
            ct);
    }

    /// <summary>
    /// Xác nhận danh sách ảnh đã upload lên Cloudinary VÀ tự động gắn các ảnh hợp lệ vào equipment_model_media.
    /// Các ảnh lỗi (sai định dạng, vượt dung lượng...) vẫn được trả về trong Results với Success = false + Message.
    /// </summary>
    public async Task<ConfirmFileUploadResponse> ConfirmAndAttachModelMediaAsync(
        Guid modelId,
        ConfirmFileUploadRequest request,
        Guid uploadedBy,
        CancellationToken ct)
    {
        if (!await db.EquipmentModels.AnyAsync(m => m.Id == modelId, ct))
            throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Không tìm thấy model thiết bị.", StatusCodes.Status404NotFound);

        var confirmResponse = await fileUploadService.ConfirmUploadAsync(request, uploadedBy, ct);


        var validFileIds = confirmResponse.Results
            .Where(r => r.Success && r.StorageFileId.HasValue)
            .Select(r => r.StorageFileId!.Value)
            .Distinct()
            .ToList();

        if (validFileIds.Count > 0)
        {
            var existingMedia = await db.EquipmentModelMedia
                .Where(m => m.EquipmentModelId == modelId)
                .ToListAsync(ct);

            var existingFileIds = existingMedia.Select(m => m.StorageFileId).ToHashSet();
            var hasPrimary = existingMedia.Any(m => m.IsPrimary);
            var nextOrder = existingMedia.Count == 0 ? 1 : existingMedia.Max(m => m.DisplayOrder) + 1;

            foreach (var fileId in validFileIds)
            {
                if (existingFileIds.Contains(fileId)) continue;

                db.EquipmentModelMedia.Add(new EquipmentModelMedia
                {
                    Id = Guid.CreateVersion7(),
                    EquipmentModelId = modelId,
                    StorageFileId = fileId,
                    MediaType = "IMAGE",
                    DisplayOrder = nextOrder++,
                    IsPrimary = !hasPrimary,
                    CreatedAt = DateTimeOffset.UtcNow
                });

                hasPrimary = true;
            }

            await db.SaveChangesAsync(ct);
        }

        return confirmResponse;
    }

    public async Task<EquipmentModelDetailResponse> GetModelDetailAsync(Guid id, CancellationToken ct)
    {
        var model = await db.EquipmentModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Không tìm thấy model thiết bị.", StatusCodes.Status404NotFound);

        var list = await BuildModelDetailListAsync([model], ct);
        return list[0];
    }

    public async Task<EquipmentModelDetailResponse> CreateModelAsync(SaveEquipmentModelRequest req, CancellationToken ct)
    {
        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.EquipmentModels.AnyAsync(m => m.Code == code, ct))
            throw new AppException("MODEL_CODE_EXISTS", "Mã model thiết bị đã tồn tại.", StatusCodes.Status409Conflict, "code");

        var now = DateTimeOffset.UtcNow;
        var model = new EquipmentModel
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = req.Name.Trim(),
            Manufacturer = req.Manufacturer?.Trim(),
            ModelNumber = req.ModelNumber?.Trim(),
            Description = req.Description?.Trim(),
            EstimatedInstallationMinutes = req.EstimatedInstallationMinutes,
            Unit = req.Unit.Trim().ToUpperInvariant(),
            RentalPriceDaily = req.RentalPriceDaily,
            RentalPriceMonthly = req.RentalPriceMonthly,
            Specifications = req.Specifications.HasValue ? req.Specifications.Value.GetRawText() : null,
            Status = EquipmentModelStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.EquipmentModels.Add(model);
        await SyncModelCategoriesAsync(model.Id, req.CategoryIds, ct);
        await db.SaveChangesAsync(ct);

        return await GetModelDetailAsync(model.Id, ct);
    }

    public async Task<EquipmentModelDetailResponse> UpdateModelAsync(Guid id, SaveEquipmentModelRequest req, CancellationToken ct)
    {
        var model = await db.EquipmentModels.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Không tìm thấy model thiết bị.", StatusCodes.Status404NotFound);

        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.EquipmentModels.AnyAsync(m => m.Code == code && m.Id != id, ct))
            throw new AppException("MODEL_CODE_EXISTS", "Mã model thiết bị đã tồn tại.", StatusCodes.Status409Conflict, "code");

        model.Code = code;
        model.Name = req.Name.Trim();
        model.Manufacturer = req.Manufacturer?.Trim();
        model.ModelNumber = req.ModelNumber?.Trim();
        model.Description = req.Description?.Trim();
        model.EstimatedInstallationMinutes = req.EstimatedInstallationMinutes;
        model.Unit = req.Unit.Trim().ToUpperInvariant();
        model.RentalPriceDaily = req.RentalPriceDaily;
        model.RentalPriceMonthly = req.RentalPriceMonthly;
        model.Specifications = req.Specifications.HasValue ? req.Specifications.Value.GetRawText() : null;
        model.UpdatedAt = DateTimeOffset.UtcNow;

        if (req.CategoryIds is not null)
            await SyncModelCategoriesAsync(id, req.CategoryIds, ct);

        await db.SaveChangesAsync(ct);
        return await GetModelDetailAsync(id, ct);
    }

    public async Task<EquipmentModelDetailResponse> UpdateModelStatusAsync(Guid id, string status, CancellationToken ct)
    {
        var model = await db.EquipmentModels.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Không tìm thấy model thiết bị.", StatusCodes.Status404NotFound);

        if (!Enum.TryParse<EquipmentModelStatus>(status, true, out var parsed))
            throw new AppException("INVALID_STATUS", "Trạng thái không hợp lệ (ACTIVE hoặc INACTIVE).", StatusCodes.Status400BadRequest, "status");

        model.Status = parsed;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetModelDetailAsync(id, ct);
    }

    public async Task<EquipmentModelMediaResponse> AttachMediaAsync(Guid modelId, AttachEquipmentModelMediaRequest req, CancellationToken ct)
    {
        if (!await db.EquipmentModels.AnyAsync(m => m.Id == modelId, ct))
            throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Không tìm thấy model thiết bị.", StatusCodes.Status404NotFound);

        var file = await db.StorageFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == req.StorageFileId, ct)
            ?? throw new AppException("STORAGE_FILE_NOT_FOUND", "File tải lên không tồn tại.", StatusCodes.Status404NotFound);

        if (req.IsPrimary)
        {
            var existingPrimaries = await db.EquipmentModelMedia
                .Where(m => m.EquipmentModelId == modelId && m.IsPrimary).ToListAsync(ct);
            foreach (var item in existingPrimaries) item.IsPrimary = false;
        }

        var maxOrder = await db.EquipmentModelMedia
            .Where(m => m.EquipmentModelId == modelId)
            .Select(m => (int?)m.DisplayOrder)
            .MaxAsync(ct) ?? 0;

        var media = new EquipmentModelMedia
        {
            Id = Guid.CreateVersion7(),
            EquipmentModelId = modelId,
            StorageFileId = req.StorageFileId,
            MediaType = req.MediaType.Trim().ToUpperInvariant() == "VIDEO" ? "VIDEO" : "IMAGE",
            DisplayOrder = maxOrder + 1,
            IsPrimary = req.IsPrimary || maxOrder == 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.EquipmentModelMedia.Add(media);
        await db.SaveChangesAsync(ct);

        return new EquipmentModelMediaResponse(media.Id, media.StorageFileId, fileUploadService.GetPrivateImageUrl($"{file.BucketName}/{file.ObjectKey}", file.ResourceType.ToCloudinaryNetResourceType(), file.ContentType!), media.MediaType, media.DisplayOrder, media.IsPrimary);
    }

    public async Task DeleteMediaAsync(Guid modelId, Guid mediaId, CancellationToken ct)
    {
        var media = await db.EquipmentModelMedia
            .FirstOrDefaultAsync(m => m.Id == mediaId && m.EquipmentModelId == modelId, ct)
            ?? throw new AppException("MEDIA_NOT_FOUND", "Không tìm thấy ảnh/video.", StatusCodes.Status404NotFound);

        db.EquipmentModelMedia.Remove(media);
        await db.SaveChangesAsync(ct);
    }
    public async Task<PagedResponse<EquipmentItemResponse>> GetEquipmentsAsync(
    Guid? modelId,
    Guid? warehouseId,
    string? status,
    string? condition,
    string? keyword,
    int page = 1,
    int pageSize = 20,
    CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = from e in db.Equipments.AsNoTracking()
                    join m in db.EquipmentModels.AsNoTracking() on e.EquipmentModelId equals m.Id
                    join w in db.Warehouses.AsNoTracking() on e.WarehouseId equals w.Id
                    select new { e, m, w };

        if (modelId.HasValue) query = query.Where(x => x.e.EquipmentModelId == modelId.Value);
        if (warehouseId.HasValue) query = query.Where(x => x.e.WarehouseId == warehouseId.Value);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<EquipmentStatus>(status, true, out var st))
            query = query.Where(x => x.e.Status == st);
        if (!string.IsNullOrWhiteSpace(condition))
            query = query.Where(x => x.e.Condition == condition.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var p = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.e.SerialNumber, p) ||
                EF.Functions.ILike(x.m.Name, p) ||
                EF.Functions.ILike(x.m.Code, p));
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.e.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new EquipmentItemResponse(
                x.e.Id, x.e.EquipmentModelId, x.m.Code, x.m.Name,
                x.e.WarehouseId, x.w.Name, x.e.SerialNumber,
                EnumName.ToDatabaseValue(x.e.Status), x.e.Condition,
                x.e.AcquiredAt, x.e.AcquisitionCost, x.e.Note, x.e.CreatedAt))
            .ToListAsync(ct);

        return new PagedResponse<EquipmentItemResponse>(items, totalCount, page, pageSize);
    }

    public async Task<EquipmentItemResponse> CreateEquipmentAsync(CreateEquipmentRequest req, CancellationToken ct)
    {
        var result = await BatchCreateEquipmentsAsync(new BatchCreateEquipmentRequest(
            req.EquipmentModelId, req.WarehouseId, [req.SerialNumber],
            req.Condition, req.AcquiredAt, req.AcquisitionCost, req.Note), ct);
        return result[0];
    }

    public async Task<List<EquipmentItemResponse>> BatchCreateEquipmentsAsync(BatchCreateEquipmentRequest req, CancellationToken ct)
    {
        var model = await db.EquipmentModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == req.EquipmentModelId, ct)
            ?? throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Model thiết bị không tồn tại.", StatusCodes.Status404NotFound);
        var warehouse = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == req.WarehouseId && w.Status == WarehouseStatus.Active, ct)
            ?? throw new AppException("WAREHOUSE_NOT_FOUND", "Kho không tồn tại hoặc đã ngừng hoạt động.", StatusCodes.Status404NotFound);

        var condition = NormalizeCondition(req.Condition);
        var serials = req.SerialNumbers
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        if (serials.Count == 0)
            throw new AppException("SERIAL_REQUIRED", "Vui lòng nhập ít nhất 1 số serial.", StatusCodes.Status400BadRequest, "serialNumbers");

        var duplicates = await db.Equipments.AsNoTracking()
            .Where(e => serials.Contains(e.SerialNumber))
            .Select(e => e.SerialNumber)
            .ToListAsync(ct);

        if (duplicates.Count > 0)
            throw new AppException("SERIAL_ALREADY_EXISTS", $"Số serial đã tồn tại: {string.Join(", ", duplicates)}", StatusCodes.Status409Conflict, "serialNumbers");

        var now = DateTimeOffset.UtcNow;
        var initialStatus = condition == "DAMAGED" ? EquipmentStatus.Damaged : EquipmentStatus.Available;

        var entities = serials.Select(sn => new Entities.Equipment
        {
            Id = Guid.CreateVersion7(),
            EquipmentModelId = model.Id,
            WarehouseId = warehouse.Id,
            SerialNumber = sn,
            Status = initialStatus,
            Condition = condition,
            AcquiredAt = req.AcquiredAt,
            AcquisitionCost = req.AcquisitionCost,
            Note = req.Note?.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();

        db.Equipments.AddRange(entities);
        await db.SaveChangesAsync(ct);

        return entities.Select(e => new EquipmentItemResponse(
            e.Id, e.EquipmentModelId, model.Code, model.Name,
            e.WarehouseId, warehouse.Name, e.SerialNumber,
            EnumName.ToDatabaseValue(e.Status), e.Condition,
            e.AcquiredAt, e.AcquisitionCost, e.Note, e.CreatedAt)).ToList();
    }

    public async Task<EquipmentItemResponse> UpdateEquipmentAsync(Guid id, UpdateEquipmentRequest req, CancellationToken ct)
    {
        var entity = await db.Equipments.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new AppException("EQUIPMENT_NOT_FOUND", "Không tìm thấy thiết bị.", StatusCodes.Status404NotFound);

        if (entity.Status is EquipmentStatus.Rented or EquipmentStatus.Reserved)
            throw new AppException("EQUIPMENT_IN_USE", "Không thể chỉnh sửa thủ công thiết bị đang được giữ chỗ hoặc đang cho thuê.", StatusCodes.Status409Conflict);

        if (!Enum.TryParse<EquipmentStatus>(req.Status, true, out var newStatus) ||
            newStatus is EquipmentStatus.Rented or EquipmentStatus.Reserved)
            throw new AppException("INVALID_STATUS", "Trạng thái không hợp lệ để cập nhật thủ công.", StatusCodes.Status400BadRequest, "status");

        var warehouse = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(w => w.Id == req.WarehouseId, ct)
            ?? throw new AppException("WAREHOUSE_NOT_FOUND", "Kho không tồn tại.", StatusCodes.Status404NotFound);

        entity.WarehouseId = req.WarehouseId;
        entity.Status = newStatus;
        entity.Condition = NormalizeCondition(req.Condition);
        entity.AcquiredAt = req.AcquiredAt;
        entity.AcquisitionCost = req.AcquisitionCost;
        entity.Note = req.Note?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var model = await db.EquipmentModels.AsNoTracking().FirstAsync(m => m.Id == entity.EquipmentModelId, ct);
        return new EquipmentItemResponse(
            entity.Id, entity.EquipmentModelId, model.Code, model.Name,
            entity.WarehouseId, warehouse.Name, entity.SerialNumber,
            EnumName.ToDatabaseValue(entity.Status), entity.Condition,
            entity.AcquiredAt, entity.AcquisitionCost, entity.Note, entity.CreatedAt);
    }

    public async Task<NextSerialPreviewResponse> GetNextSerialPreviewAsync(Guid modelId, CancellationToken ct)
    {
        var model = await db.EquipmentModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Không tìm thấy model thiết bị.", StatusCodes.Status404NotFound);

        var prefix = $"{model.Code.Trim().ToUpperInvariant()}-";
        var nextSeq = await GetNextSerialSequenceAsync(prefix, ct);
        var padLength = Math.Max(3, nextSeq.ToString().Length);

        return new NextSerialPreviewResponse(prefix, nextSeq, padLength);
    }

    public async Task<List<EquipmentItemResponse>> AutoGenerateEquipmentsAsync(
        AutoGenerateEquipmentsRequest req,
        CancellationToken ct)
    {
        var model = await db.EquipmentModels.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == req.EquipmentModelId, ct)
            ?? throw new AppException("EQUIPMENT_MODEL_NOT_FOUND", "Model thiết bị không tồn tại.", StatusCodes.Status404NotFound);

        var warehouse = req.WarehouseId.HasValue
            ? await db.Warehouses.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == req.WarehouseId.Value && w.Status == WarehouseStatus.Active, ct)
            : await db.Warehouses.AsNoTracking()
                .Where(w => w.Status == WarehouseStatus.Active)
                .OrderBy(w => w.CreatedAt)
                .FirstOrDefaultAsync(ct);

        if (warehouse is null)
            throw new AppException("WAREHOUSE_NOT_FOUND", "Chưa có kho đang hoạt động.", StatusCodes.Status404NotFound);

        var prefix = $"{model.Code.Trim().ToUpperInvariant()}-";
        var startSeq = await GetNextSerialSequenceAsync(prefix, ct);
        var padLength = Math.Max(3, (startSeq + req.Quantity - 1).ToString().Length);

        var now = DateTimeOffset.UtcNow;
        var acquiredAt = req.AcquiredAt ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

        var entities = Enumerable.Range(0, req.Quantity).Select(i => new Entities.Equipment
        {
            Id = Guid.CreateVersion7(),
            EquipmentModelId = model.Id,
            WarehouseId = warehouse.Id,
            SerialNumber = $"{prefix}{(startSeq + i).ToString().PadLeft(padLength, '0')}",
            Status = EquipmentStatus.Available,
            Condition = "GOOD",
            AcquiredAt = acquiredAt,
            AcquisitionCost = req.AcquisitionCost,
            Note = req.Note?.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();

        db.Equipments.AddRange(entities);
        await db.SaveChangesAsync(ct);

        return entities.Select(e => new EquipmentItemResponse(
            e.Id, e.EquipmentModelId, model.Code, model.Name,
            e.WarehouseId, warehouse.Name, e.SerialNumber,
            EnumName.ToDatabaseValue(e.Status), e.Condition,
            e.AcquiredAt, e.AcquisitionCost, e.Note, e.CreatedAt)).ToList();
    }

    private async Task<int> GetNextSerialSequenceAsync(string prefix, CancellationToken ct)
    {
        var existingSerials = await db.Equipments.AsNoTracking()
            .Where(e => e.SerialNumber.StartsWith(prefix))
            .Select(e => e.SerialNumber)
            .ToListAsync(ct);

        var maxNum = 0;
        foreach (var sn in existingSerials)
        {
            var suffix = sn[prefix.Length..];
            if (int.TryParse(suffix, out var num) && num > maxNum)
                maxNum = num;
        }

        return maxNum + 1;
    }

    // ==================== HELPERS ====================
    private async Task SyncModelCategoriesAsync(Guid modelId, List<Guid>? categoryIds, CancellationToken ct)
    {
        var existing = await db.EquipmentModelCategories
            .Where(x => x.EquipmentModelId == modelId).ToListAsync(ct);
        db.EquipmentModelCategories.RemoveRange(existing);

        if (categoryIds is null || categoryIds.Count == 0) return;

        var uniqueIds = categoryIds.Distinct().ToList();
        var validCount = await db.Categories.CountAsync(c => uniqueIds.Contains(c.Id), ct);
        if (validCount != uniqueIds.Count)
            throw new AppException("CATEGORY_NOT_FOUND", "Có danh mục không tồn tại.", StatusCodes.Status400BadRequest, "categoryIds");

        foreach (var catId in uniqueIds)
            db.EquipmentModelCategories.Add(new EquipmentModelCategory { EquipmentModelId = modelId, CategoryId = catId });
    }

    private async Task<List<EquipmentModelDetailResponse>> BuildModelDetailListAsync(List<EquipmentModel> models, CancellationToken ct)
    {
        if (models.Count == 0) return [];
        var modelIds = models.Select(m => m.Id).ToList();
        var today = EquipmentAvailabilityService.BusinessToday();

        var availability = await availabilityService.GetAvailabilityAsync(modelIds, today, today, cancellationToken: ct);
        var totalCounts = await db.Equipments.AsNoTracking()
            .Where(e => modelIds.Contains(e.EquipmentModelId))
            .GroupBy(e => e.EquipmentModelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var catLinks = await (from mc in db.EquipmentModelCategories.AsNoTracking()
                              join c in db.Categories.AsNoTracking() on mc.CategoryId equals c.Id
                              where modelIds.Contains(mc.EquipmentModelId)
                              select new { mc.EquipmentModelId, c }).ToListAsync(ct);

        var mediaLinks = await (from em in db.EquipmentModelMedia.AsNoTracking()
                                join sf in db.StorageFiles.AsNoTracking() on em.StorageFileId equals sf.Id
                                where modelIds.Contains(em.EquipmentModelId)
                                orderby em.IsPrimary descending, em.DisplayOrder
                                select new { em.EquipmentModelId, em, fileUrl = fileUploadService.GetPrivateImageUrl($"{sf.BucketName}/{sf.ObjectKey}", sf.ResourceType.ToCloudinaryNetResourceType(), sf.ContentType!) }).ToListAsync(ct);

        return models.Select(m =>
        {
            var avail = availability[m.Id];
            var cats = catLinks.Where(x => x.EquipmentModelId == m.Id)
                .Select(x => new CategoryResponse(x.c.Id, x.c.ParentId, x.c.Code, x.c.Name, x.c.Description, EnumName.ToDatabaseValue(x.c.Status), 0))
                .ToList();
            var media = mediaLinks.Where(x => x.EquipmentModelId == m.Id)
                .Select(x => new EquipmentModelMediaResponse(x.em.Id, x.em.StorageFileId, x.fileUrl, x.em.MediaType, x.em.DisplayOrder, x.em.IsPrimary))
                .ToList();

            JsonElement? specs = null;
            if (!string.IsNullOrWhiteSpace(m.Specifications))
            {
                using var doc = JsonDocument.Parse(m.Specifications);
                specs = doc.RootElement.Clone();
            }

            return new EquipmentModelDetailResponse(
                m.Id, m.Code, m.Name, m.Manufacturer, m.ModelNumber, m.Description,
                m.EstimatedInstallationMinutes, m.Unit, m.RentalPriceDaily, m.RentalPriceMonthly,
                specs, EnumName.ToDatabaseValue(m.Status),
                totalCounts.GetValueOrDefault(m.Id),
                avail.OperationalUnits, avail.AvailableQuantity,
                cats, media);
        }).ToList();
    }

    private static string NormalizeCondition(string condition)
    {
        var normalized = condition.Trim().ToUpperInvariant();
        if (!ValidConditions.Contains(normalized))
            throw new AppException("INVALID_CONDITION", "Tình trạng phải là GOOD, FAIR hoặc DAMAGED.", StatusCodes.Status400BadRequest, "condition");
        return normalized;
    }
}