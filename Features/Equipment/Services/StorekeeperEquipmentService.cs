using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Entities;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
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
        var trimmedName = req.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            throw new AppException("CATEGORY_NAME_REQUIRED", "Vui lòng nhập tên danh mục.", StatusCodes.Status400BadRequest, "name");

        var duplicateName = await db.Categories.AsNoTracking()
            .AnyAsync(c => c.Name.Trim().ToLower() == trimmedName.ToLower(), ct);
        if (duplicateName)
            throw new AppException("CATEGORY_NAME_EXISTS", $"Tên danh mục '{trimmedName}' đã tồn tại.", StatusCodes.Status409Conflict, "name");

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

        var nextCode = $"CAT-{(maxNum + 1):D3}";
        while (await db.Categories.AsNoTracking().AnyAsync(c => c.Code == nextCode, ct))
        {
            maxNum++;
            nextCode = $"CAT-{(maxNum + 1):D3}";
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new Category
        {
            Id = Guid.CreateVersion7(),
            ParentId = null,
            Code = nextCode,
            Name = trimmedName,
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

        var trimmedName = req.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            throw new AppException("CATEGORY_NAME_REQUIRED", "Vui lòng nhập tên danh mục.", StatusCodes.Status400BadRequest, "name");

        var duplicateName = await db.Categories.AsNoTracking()
            .AnyAsync(c => c.Id != id && c.Name.Trim().ToLower() == trimmedName.ToLower(), ct);
        if (duplicateName)
            throw new AppException("CATEGORY_NAME_EXISTS", $"Tên danh mục '{trimmedName}' đã tồn tại.", StatusCodes.Status409Conflict, "name");

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!Enum.TryParse<CategoryStatus>(req.Status.Trim(), true, out var parsedStatus))
                throw new AppException("INVALID_STATUS", "Trạng thái phải là ACTIVE hoặc INACTIVE.", StatusCodes.Status400BadRequest, "status");

            entity.Status = parsedStatus;
        }

        entity.Name = trimmedName;
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
        if (string.IsNullOrWhiteSpace(code))
            throw new AppException("MODEL_CODE_REQUIRED", "Mã model thiết bị không được để trống.", StatusCodes.Status400BadRequest, "code");

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
            Specifications = NormalizeSpecificationsJson(req.Specifications),
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
        if (string.IsNullOrWhiteSpace(code))
            throw new AppException("MODEL_CODE_REQUIRED", "Mã model thiết bị không được để trống.", StatusCodes.Status400BadRequest, "code");

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
        model.Specifications = NormalizeSpecificationsJson(req.Specifications);
        model.UpdatedAt = DateTimeOffset.UtcNow;

        if (req.CategoryIds is not null)
            await SyncModelCategoriesAsync(id, req.CategoryIds, ct);

        await db.SaveChangesAsync(ct);
        return await GetModelDetailAsync(id, ct);
    }

    /// <summary>Xuất file Excel mẫu (.xlsx) kèm sheet danh sách danh mục đang hoạt động.</summary>
    public async Task<byte[]> GenerateModelImportTemplateAsync(CancellationToken ct)
    {
        var activeCategories = await db.Categories.AsNoTracking()
            .Where(c => c.Status == CategoryStatus.Active)
            .OrderBy(c => c.Code)
            .ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("MauNhapModel");

        var headers = new[]
        {
            "TenModel (*)",
            "HangSanXuat (*)",
            "SoHieuModel (*)",
            "MaModel (De trong = Tu sinh)",
            "DonVi (*)",
            "GiaThueNgay (*)",
            "GiaThueThang (*)",
            "PhutLapDat",
            "MaDanhMuc (Cach nhau dau phay)",
            "ThongSoKyThuat (JSON)",
            "MoTa"
        };

        for (var col = 1; col <= headers.Length; col++)
        {
            var cell = ws.Cell(1, col);
            cell.Value = headers[col - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var sampleCatCode = activeCategories.FirstOrDefault()?.Code ?? "CAT-001";
        ws.Cell(2, 1).Value = "Máy chiếu Sony VPL-DX241";
        ws.Cell(2, 2).Value = "Sony";
        ws.Cell(2, 3).Value = "VPL-DX241";
        ws.Cell(2, 4).Value = "";
        ws.Cell(2, 5).Value = "UNIT";
        ws.Cell(2, 6).Value = 250000;
        ws.Cell(2, 7).Value = 4500000;
        ws.Cell(2, 8).Value = 20;
        ws.Cell(2, 9).Value = sampleCatCode;
        ws.Cell(2, 10).Value = "{\"DoSang\": \"3300 ANSI\", \"DoPhanGiai\": \"XGA\", \"CongKetNoi\": \"\"}";
        ws.Cell(2, 11).Value = "Máy chiếu văn phòng độ sáng cao, kèm remote và cáp HDMI";

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);

        var wsCat = wb.Worksheets.Add("DanhMucHeThong");
        wsCat.Cell(1, 1).Value = "MaDanhMuc";
        wsCat.Cell(1, 2).Value = "TenDanhMuc";
        wsCat.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < activeCategories.Count; i++)
        {
            wsCat.Cell(i + 2, 1).Value = activeCategories[i].Code;
            wsCat.Cell(i + 2, 2).Value = activeCategories[i].Name;
        }
        wsCat.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Import hàng loạt EquipmentModel từ file Excel (.xlsx).</summary>
    public async Task<ImportEquipmentModelsResult> ImportModelsFromExcelAsync(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            throw new AppException("FILE_EMPTY", "Vui lòng chọn file Excel (.xlsx) hợp lệ.", StatusCodes.Status400BadRequest, "file");

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new AppException("INVALID_FILE_TYPE", "Chỉ hỗ trợ định dạng Excel .xlsx.", StatusCodes.Status400BadRequest, "file");

        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var catByCode = categories.ToDictionary(c => c.Code.Trim().ToUpperInvariant(), c => c.Id);
        var catByName = categories
            .GroupBy(c => c.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Id);

        var existingCodes = (await db.EquipmentModels.AsNoTracking()
            .Select(m => m.Code)
            .ToListAsync(ct))
            .Select(c => c.Trim().ToUpperInvariant())
            .ToHashSet();

        using var stream = file.OpenReadStream();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        if (lastRow < 2)
            throw new AppException("EXCEL_NO_DATA", "File Excel không có dòng dữ liệu nào.", StatusCodes.Status400BadRequest);

        var errors = new List<ImportEquipmentModelRowError>();
        var createdEntities = new List<EquipmentModel>();
        var now = DateTimeOffset.UtcNow;
        var totalRows = 0;

        for (var rowIdx = 2; rowIdx <= lastRow; rowIdx++)
        {
            var name = ws.Cell(rowIdx, 1).GetString().Trim();
            var mfg = ws.Cell(rowIdx, 2).GetString().Trim();
            var modelNum = ws.Cell(rowIdx, 3).GetString().Trim();
            var rawCode = ws.Cell(rowIdx, 4).GetString().Trim().ToUpperInvariant();
            var unit = ws.Cell(rowIdx, 5).GetString().Trim().ToUpperInvariant();
            var dailyText = ws.Cell(rowIdx, 6).GetString().Trim();
            var monthlyText = ws.Cell(rowIdx, 7).GetString().Trim();
            var installText = ws.Cell(rowIdx, 8).GetString().Trim();
            var catText = ws.Cell(rowIdx, 9).GetString().Trim();
            var specsText = ws.Cell(rowIdx, 10).GetString().Trim();
            var desc = ws.Cell(rowIdx, 11).GetString().Trim();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(rawCode) && string.IsNullOrWhiteSpace(dailyText))
                continue;

            totalRows++;

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, rawCode, name, "Thiếu Tên Model (*)."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(mfg))
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, rawCode, name, "Thiếu Hãng sản xuất (*)."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(modelNum))
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, rawCode, name, "Thiếu Số hiệu Model (*)."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(unit))
                unit = "UNIT";

            if (!decimal.TryParse(dailyText, NumberStyles.Any, CultureInfo.InvariantCulture, out var dailyPrice) || dailyPrice < 0)
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, rawCode, name, "Giá thuê/ngày không hợp lệ."));
                continue;
            }

            if (!decimal.TryParse(monthlyText, NumberStyles.Any, CultureInfo.InvariantCulture, out var monthlyPrice) || monthlyPrice < 0)
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, rawCode, name, "Giá thuê/tháng không hợp lệ."));
                continue;
            }

            var installMins = 0;
            if (!string.IsNullOrWhiteSpace(installText) && (!int.TryParse(installText, out installMins) || installMins < 0))
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, rawCode, name, "Phút lắp đặt dự kiến phải là số nguyên >= 0."));
                continue;
            }

            var code = string.IsNullOrWhiteSpace(rawCode)
                ? GenerateAutoModelCode(mfg, modelNum, name, existingCodes)
                : rawCode;

            if (existingCodes.Contains(code))
            {
                errors.Add(new ImportEquipmentModelRowError(rowIdx, code, name, $"Mã model '{code}' đã tồn tại hoặc bị trùng trong file."));
                continue;
            }

            var catIds = new List<Guid>();
            var invalidCat = false;
            if (!string.IsNullOrWhiteSpace(catText))
            {
                foreach (var token in catText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (catByCode.TryGetValue(token.ToUpperInvariant(), out var byC))
                        catIds.Add(byC);
                    else if (catByName.TryGetValue(token.ToLowerInvariant(), out var byN))
                        catIds.Add(byN);
                    else
                    {
                        errors.Add(new ImportEquipmentModelRowError(rowIdx, code, name, $"Không tìm thấy danh mục '{token}'."));
                        invalidCat = true;
                        break;
                    }
                }
            }
            if (invalidCat) continue;

            string? specsJson = null;
            if (!string.IsNullOrWhiteSpace(specsText))
            {
                try
                {
                    specsJson = ParseFlexibleSpecsTextToJson(specsText);
                }
                catch (JsonException)
                {
                    errors.Add(new ImportEquipmentModelRowError(rowIdx, code, name, "Thông số kỹ thuật (JSON) không đúng cú pháp."));
                    continue;
                }
            }

            existingCodes.Add(code);
            var model = new EquipmentModel
            {
                Id = Guid.CreateVersion7(),
                Code = code,
                Name = name,
                Manufacturer = string.IsNullOrWhiteSpace(mfg) ? null : mfg,
                ModelNumber = string.IsNullOrWhiteSpace(modelNum) ? null : modelNum,
                Description = string.IsNullOrWhiteSpace(desc) ? null : desc,
                EstimatedInstallationMinutes = installMins,
                Unit = unit,
                RentalPriceDaily = dailyPrice,
                RentalPriceMonthly = monthlyPrice,
                Specifications = specsJson,
                Status = EquipmentModelStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };

            db.EquipmentModels.Add(model);
            foreach (var cid in catIds.Distinct())
            {
                db.EquipmentModelCategories.Add(new EquipmentModelCategory
                {
                    EquipmentModelId = model.Id,
                    CategoryId = cid,
                });
            }

            createdEntities.Add(model);
        }

        if (createdEntities.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var createdDetails = createdEntities.Count > 0
            ? await BuildModelDetailListAsync(createdEntities, ct)
            : [];

        return new ImportEquipmentModelsResult(
            totalRows,
            createdDetails.Count,
            errors.Count,
            errors,
            createdDetails);
    }

    private static string GenerateAutoModelCode(string? manufacturer, string? modelNumber, string name, HashSet<string> usedCodes)
    {
        var p1 = SanitizeCodeToken(manufacturer);
        var p2 = SanitizeCodeToken(modelNumber);
        if (string.IsNullOrEmpty(p1) && string.IsNullOrEmpty(p2))
            p1 = SanitizeCodeToken(name);
        if (p1.Length > 10) p1 = p1[..10];
        if (p2.Length > 12) p2 = p2[..12];

        var basePrefix = string.Join("-", new[] { p1, p2 }.Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrEmpty(basePrefix))
            basePrefix = "MODEL";

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var rand = Random.Shared.Next(100, 1000);
            var candidate = $"{basePrefix}-{rand}";
            if (!usedCodes.Contains(candidate))
                return candidate;
        }

        return $"{basePrefix}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";
    }

    private static string SanitizeCodeToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var normalized = input.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
                continue;

            var mapped = ch switch
            {
                'đ' or 'Đ' => 'D',
                _ => char.ToUpperInvariant(ch),
            };

            if ((mapped >= 'A' && mapped <= 'Z') || (mapped >= '0' && mapped <= '9'))
                sb.Append(mapped);
            else if (mapped is ' ' or '-' or '_')
                sb.Append('-');
        }

        return string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NormalizeSpecificationsJson(JsonElement? specs)
    {
        if (!specs.HasValue || specs.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (specs.Value.ValueKind == JsonValueKind.Object)
        {
            var obj = new JsonObject();
            foreach (var prop in specs.Value.EnumerateObject())
            {
                var key = prop.Name.Trim();
                if (string.IsNullOrEmpty(key)) continue;

                obj[key] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => JsonValue.Create(string.Empty),
                    JsonValueKind.String => JsonValue.Create(prop.Value.GetString() ?? string.Empty),
                    _ => JsonNode.Parse(prop.Value.GetRawText()),
                };
            }
            return obj.Count == 0 ? null : obj.ToJsonString();
        }

        return specs.Value.GetRawText();
    }

    private static string? ParseFlexibleSpecsTextToJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{'))
        {

            var sanitized = Regex.Replace(trimmed, @":\s*(?=[,}])", ": \"\"");
            using var doc = JsonDocument.Parse(sanitized);
            return NormalizeSpecificationsJson(doc.RootElement);
        }

        var obj = new JsonObject();
        foreach (var pair in trimmed.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = pair.IndexOf(':');
            if (idx <= 0)
            {
                obj[pair.Trim()] = string.Empty;
            }
            else
            {
                var k = pair[..idx].Trim();
                var v = pair[(idx + 1)..].Trim();
                if (!string.IsNullOrEmpty(k))
                    obj[k] = v;
            }
        }
        return obj.Count == 0 ? null : obj.ToJsonString();
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

    /// <summary>
    /// Xem chi tiết vòng đời thiết bị vật lý:
    /// lịch sử bảo trì/sửa chữa, danh sách linh kiện đã thay thế, chi phí tích lũy so với giá mua,
    /// lịch sử sự cố và lịch sử bàn giao cho thuê.
    /// </summary>
    public async Task<EquipmentLifecycleHistoryResponse> GetEquipmentHistoryAsync(
        Guid equipmentId,
        CancellationToken ct = default)
    {
        var eqRow = await (
            from e in db.Equipments.AsNoTracking()
            join m in db.EquipmentModels.AsNoTracking() on e.EquipmentModelId equals m.Id
            join w in db.Warehouses.AsNoTracking() on e.WarehouseId equals w.Id
            where e.Id == equipmentId
            select new { e, m, WarehouseName = w.Name }
        ).FirstOrDefaultAsync(ct)
            ?? throw new AppException("EQUIPMENT_NOT_FOUND", "Không tìm thấy thiết bị vật lý.", StatusCodes.Status404NotFound);

        // 1. Lịch sử bảo trì + biên bản + linh kiện đã thay + ảnh minh chứng
        var tasks = await db.MaintenanceTasks.AsNoTracking()
            .Where(t => t.EquipmentId == equipmentId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        var taskIds = tasks.Select(t => t.Id).ToList();

        var assignedStaffIds = tasks
            .Where(t => t.AssignedTechnicianStaffId.HasValue)
            .Select(t => t.AssignedTechnicianStaffId!.Value)
            .Distinct()
            .ToList();

        var assignedNames = assignedStaffIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await (
                from s in db.Staff.AsNoTracking()
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                where assignedStaffIds.Contains(s.Id)
                select new { s.Id, u.FullName }
            ).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);

        var recRows = taskIds.Count == 0
            ? []
            : await (
                from r in db.MaintenanceRecords.AsNoTracking()
                join s in db.Staff.AsNoTracking() on r.PerformedBy equals s.Id
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                where taskIds.Contains(r.MaintenanceTaskId)
                orderby r.CompletedAt descending
                select new { r, PerformedByName = u.FullName }
            ).ToListAsync(ct);

        var recIds = recRows.Select(x => x.r.Id).ToList();
        var parts = recIds.Count == 0
            ? []
            : await db.MaintenanceParts.AsNoTracking()
                .Where(p => recIds.Contains(p.MaintenanceRecordId))
                .OrderBy(p => p.Name)
                .ToListAsync(ct);

        var taskFiles = taskIds.Count == 0
            ? []
            : await (
                from att in db.MaintenanceTaskAttachments.AsNoTracking()
                join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
                where taskIds.Contains(att.MaintenanceTaskId)
                orderby att.CreatedAt
                select new { att.MaintenanceTaskId, f }
            ).ToListAsync(ct);

        var maintenanceHistory = tasks.Select(t =>
        {
            var tRecs = recRows
                .Where(x => x.r.MaintenanceTaskId == t.Id)
                .Select(x =>
                {
                    var rParts = parts
                        .Where(p => p.MaintenanceRecordId == x.r.Id)
                        .Select(p => new EquipmentMaintenancePartHistoryDto(
                            p.Id, p.Name, p.Quantity, p.UnitCost, p.TotalCost, p.Note))
                        .ToList();

                    return new EquipmentMaintenanceRecordHistoryDto(
                        x.r.Id,
                        x.PerformedByName,
                        x.r.Description,
                        x.r.LaborCost,
                        rParts.Sum(p => p.TotalCost),
                        x.r.CompletedAt,
                        x.r.Note,
                        rParts);
                })
                .ToList();

            var urls = taskFiles
                .Where(x => x.MaintenanceTaskId == t.Id)
                .Select(x => fileUploadService.GetPrivateImageUrl(
                    $"{x.f.BucketName}/{x.f.ObjectKey}",
                    x.f.ResourceType.ToCloudinaryNetResourceType(),
                    string.IsNullOrWhiteSpace(x.f.ContentType) ? "jpg" : x.f.ContentType))
                .ToList();

            return new EquipmentMaintenanceHistoryDto(
                t.Id,
                t.Type,
                EnumName.ToDatabaseValue(t.Status),
                t.Description,
                t.Result,
                t.AssignedTechnicianStaffId.HasValue
                    ? assignedNames.GetValueOrDefault(t.AssignedTechnicianStaffId.Value)
                    : null,
                t.StartedAt,
                t.CompletedAt,
                t.CreatedAt,
                tRecs.Sum(r => r.LaborCost),
                tRecs.Sum(r => r.PartsTotalCost),
                urls,
                tRecs);
        }).ToList();

        // 2. Lịch sử sự cố gắn thiết bị này
        var incRows = await (
            from ii in db.EquipmentIncidentItems.AsNoTracking()
            join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
            join co in db.Companies.AsNoTracking() on inc.CompanyId equals co.Id
            where ii.EquipmentId == equipmentId
            orderby inc.ReportedAt descending
            select new { ii, inc, CompanyName = co.Name }
        ).ToListAsync(ct);

        var incItemIds = incRows.Select(x => x.ii.Id).ToList();
        var incCharges = incItemIds.Count == 0
            ? []
            : await db.IncidentCharges.AsNoTracking()
                .Where(ic => incItemIds.Contains(ic.IncidentItemId) && ic.Status != IncidentChargeStatus.Cancelled)
                .OrderByDescending(ic => ic.CreatedAt)
                .ToListAsync(ct);

        var chargeByItem = incCharges
            .GroupBy(ic => ic.IncidentItemId)
            .ToDictionary(g => g.Key, g => g.First());

        var incidentHistory = incRows.Select(x =>
        {
            chargeByItem.TryGetValue(x.ii.Id, out var ch);
            return new EquipmentIncidentHistoryDto(
                x.inc.Id,
                x.inc.IncidentCode,
                x.CompanyName,
                EnumName.ToDatabaseValue(x.inc.Status),
                x.inc.ReportedAt,
                x.inc.ResolvedAt,
                x.inc.Description,
                x.ii.FaultType,
                x.ii.Assessment,
                x.inc.Resolution,
                ch?.Amount,
                ch is null ? null : EnumName.ToDatabaseValue(ch.Status));
        }).ToList();

        // 3. Lịch sử bàn giao & cho thuê (gần nhất trước)
        var rawRentalRows = await (
            from hi in db.HandoverItems.AsNoTracking()
            join hn in db.HandoverNotes.AsNoTracking() on hi.HandoverNoteId equals hn.Id
            join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where hi.EquipmentId == equipmentId && hn.Status != HandoverNoteStatus.Cancelled
            orderby hn.HandoverDate descending, hn.CreatedAt descending
            select new
            {
                c.Id,
                c.ContractCode,
                CompanyName = co.Name,
                c.Status,
                c.StartDate,
                c.EndDate,
                hn.HandoverCode,
                hn.HandoverType,
                hn.HandoverDate,
                HandoverStatus = hn.Status
            }
        ).Take(30).ToListAsync(ct);

        var rentalRows = rawRentalRows.Select(r => new EquipmentRentalHistoryDto(
            r.Id,
            r.ContractCode,
            r.CompanyName,
            EnumName.ToDatabaseValue(r.Status),
            r.StartDate,
            r.EndDate,
            r.HandoverCode,
            r.HandoverType,
            r.HandoverDate,
            EnumName.ToDatabaseValue(r.HandoverStatus))).ToList();

        var totalLabor = maintenanceHistory.Sum(x => x.TotalLaborCost);
        var totalParts = maintenanceHistory.Sum(x => x.TotalPartsCost);
        var totalMaintCost = totalLabor + totalParts;

        double? repairRatio = eqRow.e.AcquisitionCost.HasValue && eqRow.e.AcquisitionCost.Value > 0
            ? Math.Round((double)(totalMaintCost / eqRow.e.AcquisitionCost.Value * 100m), 1)
            : null;

        return new EquipmentLifecycleHistoryResponse(
            eqRow.e.Id,
            eqRow.e.SerialNumber,
            eqRow.m.Id,
            eqRow.m.Code,
            eqRow.m.Name,
            eqRow.e.WarehouseId,
            eqRow.WarehouseName,
            EnumName.ToDatabaseValue(eqRow.e.Status),
            eqRow.e.Condition,
            eqRow.e.AcquiredAt,
            eqRow.e.AcquisitionCost,
            eqRow.e.Note,
            rentalRows.Select(r => r.RentalContractId).Distinct().Count(),
            maintenanceHistory.Count,
            incidentHistory.Count,
            totalLabor,
            totalParts,
            totalMaintCost,
            repairRatio,
            maintenanceHistory,
            incidentHistory,
            rentalRows);
    }

}
