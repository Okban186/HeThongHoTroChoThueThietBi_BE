using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Customer.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Customer.Services;

/// <summary>
/// Khách hàng (COMPANY) báo sự cố thiết bị đang thuê và theo dõi xử lý.
///
/// - Chỉ chọn được máy công ty đang giữ: đã giao xong (phiếu bàn giao COMPLETED), đang RENTED,
///   chưa nhận trả.
/// - Sự cố mới ở trạng thái OPEN; quản lý / kỹ thuật viên xử lý ở phần của họ.
///   Khách chỉ được hủy khi còn OPEN.
/// - Ảnh đính kèm: tối đa 5 ảnh jpg/png/webp, mỗi ảnh ≤ 5 MB, do chính tài khoản tải lên.
/// - Mã sự cố SC-YYYY-NNNN.
/// </summary>
public sealed class CustomerIncidentService(
    AppDbContext db,
    IFileUploadService uploadService,
    INotificationDispatcher notificationDispatcher)
{
    private const string CodePrefix = "SC";
    private static readonly string[] ImageFormats = ["jpg", "jpeg", "png", "webp"];

    private static readonly FileUploadPolicy ImageUploadPolicy = new()
    {
        MaxFileCount = 5,
        MaxFileSizeBytes = 5 * 1024 * 1024,
        AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
        ResourceType = CloudinaryResourceType.Image,
        Folder = StorageFolder.Incident,
    };

    // ================================================================ máy đang thuê

    public async Task<List<CustomerRentedEquipmentResponse>> GetRentedEquipmentsAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from note in db.HandoverNotes.AsNoTracking()
            join contract in db.RentalContracts.AsNoTracking() on note.RentalContractId equals contract.Id
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
            where contract.CompanyId == companyId &&
                  note.Status == HandoverNoteStatus.Completed &&
                  equipment.Status == EquipmentStatus.Rented &&
                  !(from ret in db.EquipmentReturns
                    join retItem in db.EquipmentReturnItems on ret.Id equals retItem.EquipmentReturnId
                    where ret.RentalContractId == contract.Id &&
                          ret.Status != EquipmentReturnStatus.Cancelled &&
                          retItem.EquipmentId == equipment.Id
                    select retItem.Id).Any()
            orderby contract.ContractCode, model.Name, equipment.SerialNumber
            select new CustomerRentedEquipmentResponse(
                equipment.Id,
                equipment.SerialNumber,
                model.Id,
                model.Code,
                model.Name,
                contract.Id,
                contract.ContractCode)
        ).ToListAsync(cancellationToken);

        // Một máy có thể nằm ở nhiều phiếu (giao lại) — giữ một dòng.
        return rows.DistinctBy(r => r.EquipmentId).ToList();
    }

    /// <summary>Tra cứu chính xác máy đang thuê theo số Serial (quét barcode hoặc nhập tay) để khách báo sự cố nhanh.</summary>
    public async Task<CustomerRentedEquipmentResponse> LookupRentedSerialAsync(
        Guid companyId,
        string serial,
        CancellationToken cancellationToken = default)
    {
        var normalized = (serial ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new AppException(
                "SERIAL_REQUIRED",
                "Vui lòng nhập hoặc quét mã serial thiết bị.",
                StatusCodes.Status400BadRequest,
                "serial");
        }

        var eq = await db.Equipments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.SerialNumber.ToLower() == normalized.ToLower(), cancellationToken);

        if (eq is null)
        {
            throw new AppException(
                "SERIAL_NOT_FOUND",
                $"Không tìm thấy thiết bị có mã serial {normalized}.", StatusCodes.Status404NotFound,
                "serial");
        }

        var rented = await GetRentedEquipmentsAsync(companyId, cancellationToken);
        var match = rented.FirstOrDefault(r => r.EquipmentId == eq.Id);

        if (match is null)
        {
            throw new AppException(
                "EQUIPMENT_NOT_RENTED_BY_COMPANY",
                $"Thiết bị {eq.SerialNumber} hiện không nằm trong danh sách máy công ty bạn đang thuê (hoặc đã trả lại kho).",
                StatusCodes.Status409Conflict,
                "serial");
        }

        return match;
    }

    // ================================================================ ảnh

    public Task<FileUploadTicket> CreateUploadTicketAsync(
        FileUploadRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.CreateUploadTicketAsync(ImageUploadPolicy, request, currentUserId, cancellationToken);

    public Task<ConfirmFileUploadResponse> ConfirmUploadAsync(
        ConfirmFileUploadRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.ConfirmUploadAsync(request, currentUserId, cancellationToken);

    // ================================================================ sự cố

    public async Task<List<CustomerIncidentResponse>> GetIncidentsAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        var incidents = await db.EquipmentIncidents
            .AsNoTracking()
            .Where(i => i.CompanyId == companyId)
            .OrderByDescending(i => i.ReportedAt)
            .ToListAsync(cancellationToken);

        return await ToResponsesAsync(incidents, cancellationToken);
    }

    public async Task<CustomerIncidentDetailResponse> GetIncidentAsync(
        Guid id,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        var incident = await db.EquipmentIncidents
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId, cancellationToken)
            ?? throw NotFound();

        var items = await (
            from item in db.EquipmentIncidentItems.AsNoTracking()
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
            where item.IncidentId == id
            orderby equipment.SerialNumber
            select new CustomerIncidentItemResponse(
                equipment.Id, equipment.SerialNumber, model.Name, item.Description, item.Assessment)
        ).ToListAsync(cancellationToken);

        var files = await (
            from attachment in db.EquipmentIncidentAttachments.AsNoTracking()
            join file in db.StorageFiles.AsNoTracking() on attachment.StorageFileId equals file.Id
            where attachment.IncidentId == id
            orderby attachment.CreatedAt
            select file
        ).ToListAsync(cancellationToken);

        return new CustomerIncidentDetailResponse(
            (await ToResponsesAsync([incident], cancellationToken)).Single(),
            items,
            files.Select(f => new CustomerIncidentAttachmentResponse(
                f.Id,
                uploadService.GetPrivateImageUrl(
                    $"{f.BucketName}/{f.ObjectKey}",
                    f.ResourceType.ToCloudinaryNetResourceType(),
                    f.ContentType!))).ToList());
    }

    public async Task<CustomerIncidentDetailResponse> CreateAsync(
        CreateCustomerIncidentRequest request,
        Guid companyId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var equipmentIds = request.Items.Select(i => i.EquipmentId!.Value).ToList();

        if (equipmentIds.Distinct().Count() != equipmentIds.Count)
        {
            throw new AppException("DUPLICATE_EQUIPMENT", "Một máy chỉ chọn một lần.",
                StatusCodes.Status400BadRequest, "items");
        }

        var rented = (await GetRentedEquipmentsAsync(companyId, cancellationToken))
            .Select(e => e.EquipmentId)
            .ToHashSet();

        if (equipmentIds.Any(id => !rented.Contains(id)))
        {
            throw new AppException(
                "EQUIPMENT_NOT_RENTED_BY_COMPANY",
                "Chỉ báo sự cố cho máy công ty đang thuê.",
                StatusCodes.Status400BadRequest,
                "items");
        }

        await EnsureImagesUsableAsync(request.AttachmentFileIds, currentUserId, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        var incident = new EquipmentIncident
        {
            Id = Guid.CreateVersion7(),
            IncidentCode = await GenerateCodeAsync(cancellationToken),
            CompanyId = companyId,
            Description = request.Description.Trim(),
            ReportedAt = now,
            Status = EquipmentIncidentStatus.Open,
            ReportedBy = currentUserId,
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.EquipmentIncidents.Add(incident);

        foreach (var item in request.Items)
        {
            db.EquipmentIncidentItems.Add(new EquipmentIncidentItem
            {
                Id = Guid.CreateVersion7(),
                IncidentId = incident.Id,
                EquipmentId = item.EquipmentId!.Value,
                Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
            });
        }

        foreach (var fileId in request.AttachmentFileIds.Distinct())
        {
            db.EquipmentIncidentAttachments.Add(new EquipmentIncidentAttachment
            {
                Id = Guid.CreateVersion7(),
                IncidentId = incident.Id,
                StorageFileId = fileId,
                CreatedAt = now,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new AppException("DUPLICATE_INCIDENT_CODE", "Mã sự cố bị trùng, vui lòng thử lại.",
                StatusCodes.Status409Conflict);
        }

        // Thông báo tới Quản lý phụ trách hợp đồng của các máy gặp sự cố (fallback toàn bộ Manager)
        var assignedManagerIds = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
            where equipmentIds.Contains(hi.EquipmentId)
                  && c.CompanyId == companyId
                  && c.ApprovedBy != null
            select c.ApprovedBy!.Value
        ).Distinct().ToListAsync(cancellationToken);

        if (assignedManagerIds.Count == 0)
        {
            assignedManagerIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER"
                select ur.UserId
            ).Distinct().ToListAsync(cancellationToken);
        }

        if (assignedManagerIds.Count > 0)
        {
            var companyName = await db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? "Khách hàng";

            await notificationDispatcher.NotifyUsersAsync(
                assignedManagerIds,
                $"Khách hàng báo sự cố mới ({incident.IncidentCode})",
                $"{companyName} vừa báo sự cố {incident.IncidentCode} ({equipmentIds.Count} thiết bị). Liên hệ: {incident.ContactName} ({incident.ContactPhone}). Mô tả: {incident.Description}",
                "INCIDENT_REPORTED",
                cancellationToken: cancellationToken);
        }

        return await GetIncidentAsync(incident.Id, companyId, cancellationToken);
    }

    /// <summary>Khách hủy sự cố khi chưa được tiếp nhận xử lý (OPEN).</summary>
    public async Task<CustomerIncidentDetailResponse> CancelAsync(
        Guid id,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        var incident = await db.EquipmentIncidents
            .SingleOrDefaultAsync(i => i.Id == id && i.CompanyId == companyId, cancellationToken)
            ?? throw NotFound();

        if (incident.Status != EquipmentIncidentStatus.Open)
        {
            throw new AppException(
                "INCIDENT_NOT_CANCELLABLE",
                "Sự cố đã được tiếp nhận xử lý, vui lòng liên hệ bên cho thuê để hủy.",
                StatusCodes.Status409Conflict);
        }

        incident.Status = EquipmentIncidentStatus.Cancelled;
        incident.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return await GetIncidentAsync(id, companyId, cancellationToken);
    }

    // ================================================================ hỗ trợ

    private async Task<List<CustomerIncidentResponse>> ToResponsesAsync(
        List<EquipmentIncident> incidents,
        CancellationToken cancellationToken)
    {
        var ids = incidents.Select(i => i.Id).ToList();
        var reporterIds = incidents.Where(i => i.ReportedBy != null).Select(i => i.ReportedBy!.Value).Distinct().ToList();

        var counts = await db.EquipmentIncidentItems.AsNoTracking()
            .Where(i => ids.Contains(i.IncidentId))
            .GroupBy(i => i.IncidentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var reporters = await db.Users.AsNoTracking()
            .Where(u => reporterIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return incidents.Select(i => new CustomerIncidentResponse(
            i.Id,
            i.IncidentCode,
            i.Description,
            EnumName.ToDatabaseValue(i.Status),
            i.ReportedAt,
            i.ResolvedAt,
            i.Resolution,
            i.ContactName,
            i.ContactPhone,
            i.ReportedBy is null ? null : reporters.GetValueOrDefault(i.ReportedBy.Value),
            counts.GetValueOrDefault(i.Id))).ToList();
    }

    private async Task EnsureImagesUsableAsync(
        List<Guid> fileIds,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
            return;

        var ids = fileIds.Distinct().ToList();

        var files = await db.StorageFiles
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Select(f => new { f.UploadedBy, f.ContentType })
            .ToListAsync(cancellationToken);

        if (files.Count != ids.Count || files.Any(f => f.UploadedBy != currentUserId))
        {
            throw new AppException("INCIDENT_FILE_INVALID", "Ảnh không hợp lệ hoặc không phải do bạn tải lên.",
                StatusCodes.Status400BadRequest, "attachmentFileIds");
        }

        if (files.Any(f => !ImageFormats.Contains((f.ContentType ?? "").ToLowerInvariant())))
        {
            throw new AppException("INCIDENT_FILE_FORMAT_INVALID", "Ảnh sự cố phải là jpg, png hoặc webp.",
                StatusCodes.Status400BadRequest, "attachmentFileIds");
        }

        if (await db.EquipmentIncidentAttachments.AnyAsync(a => ids.Contains(a.StorageFileId), cancellationToken))
        {
            throw new AppException("INCIDENT_FILE_IN_USE", "Ảnh đã được dùng cho sự cố khác.",
                StatusCodes.Status409Conflict, "attachmentFileIds");
        }
    }

    private async Task<string> GenerateCodeAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{CodePrefix}-{EquipmentAvailabilityService.BusinessToday().Year}-";

        var codes = await db.EquipmentIncidents
            .AsNoTracking()
            .Where(i => i.IncidentCode.StartsWith(prefix))
            .Select(i => i.IncidentCode)
            .ToListAsync(cancellationToken);

        var max = codes
            .Select(code => int.TryParse(code[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{max + 1:D4}";
    }

    private static AppException NotFound() =>
        new("INCIDENT_NOT_FOUND", "Không tìm thấy sự cố.", StatusCodes.Status404NotFound);
}
