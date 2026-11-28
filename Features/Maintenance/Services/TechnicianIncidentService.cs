using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Maintenance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Configurations;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Services;

/// <summary>
/// Nghiệp vụ xử lý sự cố thiết bị cho Kỹ thuật viên (Hỗ trợ Online & Cử đội tại hiện trường):
/// - Hỗ trợ Online: KTV được phân công (RemoteTechnicianStaffId) tự xử lý & chốt kết quả.
/// - Hỗ trợ tại hiện trường: chỉ KTV trưởng (LEADER) của ServiceAssignment mới có quyền bấm Đã tới hiện trường (ON_SITE) và Chốt kết quả (RESOLVED / RETURN_REQUIRED).
/// - Thành viên đi cùng vẫn xem chi tiết và bấm mở Google Maps chỉ đường từng chặng bình thường.
/// </summary>
public sealed class TechnicianIncidentService(
    AppDbContext db,
    IFileUploadService uploadService,
    INotificationDispatcher notificationDispatcher,
    IOptions<ShippingOptions> shippingOptions)
{
    private static readonly string[] AllowedImageFormats = ["jpg", "jpeg", "png", "webp"];
    private readonly ShippingOptions _shipping = shippingOptions.Value;

    public async Task<List<TechnicianIncidentSummaryResponse>> GetMyIncidentsAsync(
        Guid currentUserId,
        string? status,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var myAssignRoles = await db.ServiceAssignmentMembers.AsNoTracking()
            .Where(m => m.StaffId == staffId)
            .ToDictionaryAsync(m => m.ServiceAssignmentId, m => m.Role, ct);

        var myAssignIds = myAssignRoles.Keys.ToList();

        var query = db.EquipmentIncidents.AsNoTracking()
            .Where(i => i.RemoteTechnicianStaffId == staffId
                        || (i.ServiceAssignmentId != null && myAssignIds.Contains(i.ServiceAssignmentId.Value)));

        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = ParseIncidentStatus(status);
            query = query.Where(i => i.Status == parsed);
        }

        var incidents = await query
            .OrderByDescending(i => i.Status == EquipmentIncidentStatus.OnSite)
            .ThenByDescending(i => i.Status == EquipmentIncidentStatus.Dispatching)
            .ThenByDescending(i => i.Status == EquipmentIncidentStatus.RemoteSupport)
            .ThenByDescending(i => i.ReportedAt)
            .ToListAsync(ct);

        if (incidents.Count == 0) return [];

        var incidentIds = incidents.Select(i => i.Id).ToList();
        var companyIds = incidents.Select(i => i.CompanyId).Distinct().ToList();
        var assignIds = incidents.Where(i => i.ServiceAssignmentId != null)
            .Select(i => i.ServiceAssignmentId!.Value).Distinct().ToList();

        var companies = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        var assignments = assignIds.Count == 0
            ? new Dictionary<Guid, ServiceAssignment>()
            : await db.ServiceAssignments.AsNoTracking()
                .Where(a => assignIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, ct);

        var items = await (
            from ii in db.EquipmentIncidentItems.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on ii.EquipmentId equals eq.Id
            where incidentIds.Contains(ii.IncidentId)
            orderby eq.SerialNumber
            select new { ii.IncidentId, eq.SerialNumber }
        ).ToListAsync(ct);

        return incidents.Select(i =>
        {
            companies.TryGetValue(i.CompanyId, out var co);
            ServiceAssignment? assign = null;
            if (i.ServiceAssignmentId.HasValue)
            {
                assignments.TryGetValue(i.ServiceAssignmentId.Value, out assign);
            }

            var isOnSite = i.ServiceAssignmentId.HasValue && myAssignRoles.ContainsKey(i.ServiceAssignmentId.Value);
            var isLeader = isOnSite
                ? string.Equals(myAssignRoles[i.ServiceAssignmentId!.Value], "LEADER", StringComparison.OrdinalIgnoreCase)
                : i.RemoteTechnicianStaffId == staffId;

            var serials = items.Where(x => x.IncidentId == i.Id).Select(x => x.SerialNumber).ToList();

            return new TechnicianIncidentSummaryResponse(
                i.Id,
                i.IncidentCode,
                isOnSite ? "ON_SITE" : "REMOTE",
                isLeader,
                i.CompanyId,
                co?.Name ?? "",
                co?.Address,
                i.ContactName,
                i.ContactPhone,
                i.Description,
                EnumName.ToDatabaseValue(i.Status),
                i.ReportedAt,
                i.ServiceAssignmentId,
                assign is null ? null : EnumName.ToDatabaseValue(assign.Status),
                assign?.ScheduledStartAt,
                assign?.EstimatedDurationMinutes,
                serials.Count,
                serials);
        }).ToList();
    }

    public async Task<TechnicianIncidentDetailResponse> GetIncidentDetailAsync(
        Guid incidentId,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var incident = await db.EquipmentIncidents.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw new AppException("INCIDENT_NOT_FOUND", "Không tìm thấy sự cố.", StatusCodes.Status404NotFound);

        var (isOnSite, isLeader) = await ResolveTechnicianAccessAsync(incident, staffId, ct);

        var company = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == incident.CompanyId, ct);

        ServiceAssignment? assignment = null;
        var members = new List<TechnicianAssignmentMemberDto>();
        if (incident.ServiceAssignmentId.HasValue)
        {
            assignment = await db.ServiceAssignments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == incident.ServiceAssignmentId.Value, ct);

            members = await (
                from m in db.ServiceAssignmentMembers.AsNoTracking()
                join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                where m.ServiceAssignmentId == incident.ServiceAssignmentId.Value
                orderby m.Role == "LEADER" descending, u.FullName
                select new TechnicianAssignmentMemberDto(s.Id, s.EmployeeCode, u.FullName, u.Phone, m.Role)
            ).ToListAsync(ct);
        }

        var itemRows = await (
            from ii in db.EquipmentIncidentItems.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on ii.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            where ii.IncidentId == incidentId
            orderby m.Name, eq.SerialNumber
            select new { ii, eq, m }
        ).ToListAsync(ct);

        var eqIds = itemRows.Select(x => x.eq.Id).ToList();

        // Tìm hợp đồng & địa chỉ/tọa độ giao gần nhất của các máy bị sự cố để KTV mở Google Maps chính xác điểm đặt máy
        var latestHandoverRows = eqIds.Count == 0
            ? []
            : await (
                from hi in db.HandoverItems.AsNoTracking()
                join hn in db.HandoverNotes.AsNoTracking() on hi.HandoverNoteId equals hn.Id
                join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
                where eqIds.Contains(hi.EquipmentId)
                      && c.CompanyId == incident.CompanyId
                      && hn.Status == HandoverNoteStatus.Completed
                orderby hn.UpdatedAt descending
                select new
                {
                    hi.EquipmentId,
                    c.Id,
                    c.ContractCode,
                    c.DeliveryAddress,
                    hn.DeliveryLatitude,
                    hn.DeliveryLongitude
                }
            ).ToListAsync(ct);

        var latestByEq = latestHandoverRows
            .GroupBy(x => x.EquipmentId)
            .ToDictionary(g => g.Key, g => g.First());

        var firstLoc = latestHandoverRows.FirstOrDefault(x => x.DeliveryLatitude.HasValue && x.DeliveryLongitude.HasValue)
                       ?? latestHandoverRows.FirstOrDefault();

        var serviceAddress = !string.IsNullOrWhiteSpace(firstLoc?.DeliveryAddress)
            ? firstLoc.DeliveryAddress
            : company?.Address;

        var files = await (
            from att in db.EquipmentIncidentAttachments.AsNoTracking()
            join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
            where att.IncidentId == incidentId
            orderby att.CreatedAt
            select f
        ).ToListAsync(ct);

        var attachmentUrls = files.Select(f => uploadService.GetPrivateImageUrl(
            $"{f.BucketName}/{f.ObjectKey}",
            f.ResourceType.ToCloudinaryNetResourceType(),
            f.ContentType ?? "jpg")).ToList();

        var depot = await ResolveDepotAsync(ct);

        var itemDtos = itemRows.Select(x =>
        {
            latestByEq.TryGetValue(x.eq.Id, out var contractInfo);
            return new TechnicianIncidentItemDetailDto(
                x.ii.Id,
                x.eq.Id,
                x.eq.SerialNumber,
                x.m.Id,
                x.m.Code,
                x.m.Name,
                EnumName.ToDatabaseValue(x.eq.Status),
                x.eq.Condition,
                x.ii.Description,
                x.ii.Assessment,
                x.ii.Note,
                contractInfo?.Id,
                contractInfo?.ContractCode);
        }).ToList();

        return new TechnicianIncidentDetailResponse(
            incident.Id,
            incident.IncidentCode,
            isOnSite ? "ON_SITE" : "REMOTE",
            isLeader,
            incident.CompanyId,
            company?.Name ?? "",
            serviceAddress,
            firstLoc?.DeliveryLatitude,
            firstLoc?.DeliveryLongitude,
            incident.ContactName,
            incident.ContactPhone,
            incident.Description,
            EnumName.ToDatabaseValue(incident.Status),
            incident.Resolution,
            incident.ReportedAt,
            incident.ResolvedAt,
            incident.ServiceAssignmentId,
            assignment is null ? null : EnumName.ToDatabaseValue(assignment.Status),
            assignment?.ScheduledStartAt,
            assignment?.EstimatedDurationMinutes,
            assignment?.StartedAt,
            assignment?.CompletedAt,
            depot,
            members,
            itemDtos,
            attachmentUrls);
    }

    /// <summary>Chỉ KTV trưởng (LEADER): xác nhận đã tới hiện trường xử lý sự cố (DISPATCHING → ON_SITE).</summary>
    public async Task<TechnicianIncidentDetailResponse> StartOnSiteAsync(
        Guid incidentId,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var incident = await db.EquipmentIncidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw new AppException("INCIDENT_NOT_FOUND", "Không tìm thấy sự cố.", StatusCodes.Status404NotFound);

        var (isOnSite, isLeader) = await ResolveTechnicianAccessAsync(incident, staffId, ct);
        if (!isOnSite || !isLeader)
        {
            throw new AppException(
                "LEADER_ONLY_ACTION",
                "Chỉ Kỹ thuật viên trưởng của đội xử lý hiện trường mới được phép bấm bắt đầu.",
                StatusCodes.Status403Forbidden);
        }

        if (incident.Status != EquipmentIncidentStatus.Dispatching)
        {
            throw new AppException(
                "INVALID_STATUS",
                "Chỉ sự cố đang điều phối đội (DISPATCHING) mới chuyển sang Đang xử lý tại chỗ (ON_SITE).",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        incident.Status = EquipmentIncidentStatus.OnSite;
        incident.UpdatedAt = now;

        if (incident.ServiceAssignmentId.HasValue)
        {
            var assign = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == incident.ServiceAssignmentId.Value, ct);
            if (assign is not null && assign.Status == ServiceAssignmentStatus.Pending)
            {
                assign.Status = ServiceAssignmentStatus.InProgress;
                assign.StartedAt = now;
                assign.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);

        await notificationDispatcher.NotifyCompanyAsync(
            incident.CompanyId,
            $"Kỹ thuật viên đang xử lý sự cố {incident.IncidentCode}",
            $"Đội kỹ thuật đã tiếp cận hiện trường và bắt đầu kiểm tra xử lý sự cố {incident.IncidentCode}.",
            "INCIDENT_ON_SITE",
            cancellationToken: ct);

        return await GetIncidentDetailAsync(incidentId, currentUserId, ct);
    }

    /// <summary>
    /// Chốt xử lý sự cố (Hỗ trợ Online: KTV được giao; Hiện trường: chỉ KTV trưởng LEADER):
    /// - RESOLVED: Đã khắc phục xong, đóng sự cố & đóng chuyến đi.
    /// - RETURN_REQUIRED: Không sửa được tại chỗ, tự động sinh phiếu thu hồi PROCESSING (INCIDENT_RETURN) để mang máy về kho bảo dưỡng.
    /// </summary>
    public async Task<TechnicianResolveIncidentResponse> ResolveIncidentAsync(
        Guid incidentId,
        TechnicianResolveIncidentRequest request,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);
        var outcome = request.Outcome.Trim().ToUpperInvariant();
        if (outcome is not ("RESOLVED" or "RETURN_REQUIRED" or "REMOTE_FAILED"))
        {
            throw new AppException(
                "INVALID_OUTCOME",
                "Kết quả xử lý sự cố phải là RESOLVED, REMOTE_FAILED hoặc RETURN_REQUIRED.",
                StatusCodes.Status400BadRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var incident = await db.EquipmentIncidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw new AppException("INCIDENT_NOT_FOUND", "Không tìm thấy sự cố.", StatusCodes.Status404NotFound);

        var (isOnSite, isLeader) = await ResolveTechnicianAccessAsync(incident, staffId, ct);
        if (!isLeader)
        {
            throw new AppException(
                "LEADER_ONLY_ACTION",
                "Chỉ Kỹ thuật viên trưởng (hoặc KTV phụ trách hỗ trợ Online) mới được phép chốt xử lý sự cố.",
                StatusCodes.Status403Forbidden);
        }

        if (incident.Status is not (EquipmentIncidentStatus.RemoteSupport or EquipmentIncidentStatus.Dispatching or EquipmentIncidentStatus.OnSite))
        {
            throw new AppException(
                "INVALID_STATUS",
                "Sự cố không ở trạng thái cho phép cập nhật kết quả xử lý.",
                StatusCodes.Status409Conflict);
        }

        // Phân tách chặt chẽ luồng Hỗ trợ Online vs Xử lý tại hiện trường:
        // - Hỗ trợ Online CHỈ được chọn RESOLVED (thành công) hoặc REMOTE_FAILED (không khắc phục được -> báo Quản lý).
        // - Tại hiện trường CHỈ được chọn RESOLVED hoặc RETURN_REQUIRED (thu hồi máy về kho).
        if (!isOnSite && outcome == "RETURN_REQUIRED")
        {
            throw new AppException(
                "REMOTE_CANNOT_REQUEST_RETURN",
                "Hỗ trợ từ xa không được tạo yêu cầu thu hồi trực tiếp. Nếu không khắc phục được online, hãy chọn \"Không khắc phục được\" và ghi rõ lý do để Quản lý phân công lại hoặc cử đội tới hiện trường.",
                StatusCodes.Status400BadRequest);
        }

        if (isOnSite && outcome == "REMOTE_FAILED")
        {
            throw new AppException(
                "INVALID_ONSITE_OUTCOME",
                "Đội xử lý tại hiện trường vui lòng chọn Đã khắc phục xong (RESOLVED) hoặc Thu hồi về kho (RETURN_REQUIRED).",
                StatusCodes.Status400BadRequest);
        }

        var requiresPhoto = isOnSite || outcome == "RETURN_REQUIRED";
        if (requiresPhoto && request.AttachmentFileIds.Count == 0)
        {
            throw new AppException(
                "INCIDENT_PHOTO_REQUIRED",
                "Vui lòng chụp ít nhất 1 ảnh minh chứng xử lý tại hiện trường / tình trạng máy cần thu hồi.",
                StatusCodes.Status400BadRequest);
        }

        await EnsureValidUploadedImagesAsync(request.AttachmentFileIds.ToHashSet(), currentUserId, ct);

        var now = DateTimeOffset.UtcNow;

        // 1. Lưu ảnh minh chứng mới vào EquipmentIncidentAttachments
        var existingFileIds = (await db.EquipmentIncidentAttachments
            .Where(a => a.IncidentId == incident.Id)
            .Select(a => a.StorageFileId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var fileId in request.AttachmentFileIds.Distinct())
        {
            if (existingFileIds.Add(fileId))
            {
                db.EquipmentIncidentAttachments.Add(new EquipmentIncidentAttachment
                {
                    Id = Guid.CreateVersion7(),
                    IncidentId = incident.Id,
                    StorageFileId = fileId,
                    CreatedAt = now,
                });
            }
        }

        // 2. Cập nhật đánh giá kỹ thuật cho từng máy trong sự cố
        var incidentItems = await db.EquipmentIncidentItems
            .Where(ii => ii.IncidentId == incident.Id)
            .ToListAsync(ct);

        var assessByEq = request.ItemAssessments
            .Where(a => a.EquipmentId.HasValue)
            .GroupBy(a => a.EquipmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Last());

        var returnEqIds = new HashSet<Guid>();
        foreach (var item in incidentItems)
        {
            if (assessByEq.TryGetValue(item.EquipmentId, out var a))
            {
                if (!string.IsNullOrWhiteSpace(a.Assessment))
                    item.Assessment = a.Assessment.Trim();
                if (!string.IsNullOrWhiteSpace(a.Note))
                    item.Note = a.Note.Trim();
                if (outcome == "RETURN_REQUIRED" && a.NeedsReturn)
                    returnEqIds.Add(item.EquipmentId);
            }
            else if (outcome == "RETURN_REQUIRED")
            {
                returnEqIds.Add(item.EquipmentId);
            }
        }

        if (outcome == "RETURN_REQUIRED" && returnEqIds.Count == 0)
        {
            foreach (var item in incidentItems) returnEqIds.Add(item.EquipmentId);
        }

        // 3. Nếu cần thu hồi về kho → tự sinh phiếu thu hồi PROCESSING (INCIDENT_RETURN) theo từng hợp đồng
        var autoReturnCodes = new List<string>();
        if (outcome == "RETURN_REQUIRED" && returnEqIds.Count > 0)
        {
            var eqIdList = returnEqIds.ToList();
            var eqToContractRows = await (
                from hi in db.HandoverItems.AsNoTracking()
                join hn in db.HandoverNotes.AsNoTracking() on hi.HandoverNoteId equals hn.Id
                join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
                where eqIdList.Contains(hi.EquipmentId)
                      && c.CompanyId == incident.CompanyId
                      && hn.Status == HandoverNoteStatus.Completed
                orderby hn.UpdatedAt descending
                select new { hi.EquipmentId, ContractId = c.Id }
            ).ToListAsync(ct);

            var contractByEq = eqToContractRows
                .GroupBy(x => x.EquipmentId)
                .ToDictionary(g => g.Key, g => g.First().ContractId);

            var equipments = await db.Equipments
                .Where(e => eqIdList.Contains(e.Id))
                .ToListAsync(ct);

            foreach (var eq in equipments)
            {
                eq.Condition = "DAMAGED";
                eq.UpdatedAt = now;
            }

            var returnPrefix = $"TH-{EquipmentAvailabilityService.BusinessToday().Year}-";
            var existingReturnCodes = await db.EquipmentReturns.AsNoTracking()
                .Where(r => r.ReturnCode.StartsWith(returnPrefix))
                .Select(r => r.ReturnCode)
                .ToListAsync(ct);
            var maxRet = existingReturnCodes
                .Select(c => int.TryParse(c[returnPrefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            foreach (var group in contractByEq.GroupBy(kv => kv.Value))
            {
                maxRet++;
                var code = $"{returnPrefix}{maxRet:D4}";
                var retReceipt = new EquipmentReturn
                {
                    Id = Guid.CreateVersion7(),
                    ReturnCode = code,
                    RentalContractId = group.Key,
                    ReturnedAt = now,
                    ReceivedBy = currentUserId,
                    Status = EquipmentReturnStatus.Processing,
                    ReturnType = "INCIDENT_RETURN",
                    Note = $"Thu hồi tự động theo sự cố {incident.IncidentCode}: {request.Resolution.Trim()}",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.EquipmentReturns.Add(retReceipt);
                autoReturnCodes.Add(code);

                foreach (var kv in group)
                {
                    assessByEq.TryGetValue(kv.Key, out var a);
                    db.EquipmentReturnItems.Add(new EquipmentReturnItem
                    {
                        Id = Guid.CreateVersion7(),
                        EquipmentReturnId = retReceipt.Id,
                        EquipmentId = kv.Key,
                        Status = EquipmentReturnItemStatus.Returned,
                        Note = string.IsNullOrWhiteSpace(a?.Assessment)
                            ? $"Thu hồi từ sự cố {incident.IncidentCode}"
                            : a.Assessment.Trim(),
                    });
                }
            }
        }

        // 4. Cập nhật trạng thái sự cố & chuyến đi
        var techName = await db.Users.AsNoTracking()
            .Where(u => u.Id == currentUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(ct) ?? "Kỹ thuật viên";

        if (outcome == "REMOTE_FAILED")
        {
            var reasonText = request.Resolution.Trim();
            incident.Resolution = $"[Hỗ trợ Online chưa khắc phục được - {techName}]: {reasonText}";
            incident.Status = EquipmentIncidentStatus.Open;
            incident.RemoteTechnicianStaffId = null;
            incident.UpdatedAt = now;
        }
        else
        {
            incident.Resolution = request.Resolution.Trim();
            incident.Status = outcome == "RESOLVED"
                ? EquipmentIncidentStatus.Resolved
                : EquipmentIncidentStatus.ReturnRequired;
            if (outcome == "RESOLVED")
            {
                incident.ResolvedAt = now;
            }
            incident.UpdatedAt = now;

            if (incident.ServiceAssignmentId.HasValue)
            {
                var assign = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == incident.ServiceAssignmentId.Value, ct);
                if (assign is not null && assign.Status != ServiceAssignmentStatus.Completed)
                {
                    assign.StartedAt ??= now;
                    assign.Status = ServiceAssignmentStatus.Completed;
                    assign.CompletedAt = now;
                    assign.UpdatedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // 5. Thông báo khách hàng & Quản lý/Kho
        if (outcome == "RESOLVED")
        {
            await notificationDispatcher.NotifyCompanyAsync(
                incident.CompanyId,
                $"Sự cố {incident.IncidentCode} đã được xử lý xong",
                $"Kỹ thuật viên đã khắc phục xong sự cố {incident.IncidentCode}. Kết quả: {incident.Resolution}",
                "INCIDENT_RESOLVED",
                cancellationToken: ct);
        }
        else if (outcome == "REMOTE_FAILED")
        {
            var managerIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER"
                select ur.UserId
            ).Distinct().ToListAsync(ct);

            if (managerIds.Count > 0)
            {
                await notificationDispatcher.NotifyUsersAsync(
                    managerIds,
                    $"Hỗ trợ Online không khắc phục được ({incident.IncidentCode})",
                    $"{techName} báo không thể khắc phục online sự cố {incident.IncidentCode}. Lý do: {request.Resolution.Trim()}. Vui lòng phân công KTV khác hoặc cử đội tới hiện trường.",
                    "INCIDENT_REMOTE_FAILED",
                    cancellationToken: ct);
            }
        }
        else
        {
            var codesText = autoReturnCodes.Count > 0 ? $" (Phiếu thu hồi: {string.Join(", ", autoReturnCodes)})" : "";
            await notificationDispatcher.NotifyCompanyAsync(
                incident.CompanyId,
                $"Yêu cầu thu hồi thiết bị sự cố {incident.IncidentCode}",
                $"Thiết bị gặp sự cố {incident.IncidentCode} cần được đưa về kho để sửa chữa chuyên sâu{codesText}. Ghi chú: {incident.Resolution}",
                "INCIDENT_RETURN_REQUIRED",
                cancellationToken: ct);

            var staffNotifyIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER" || r.Code == "LEAD_STOREKEEPER"
                select ur.UserId
            ).Distinct().ToListAsync(ct);

            if (staffNotifyIds.Count > 0)
            {
                await notificationDispatcher.NotifyUsersAsync(
                    staffNotifyIds,
                    $"Thu hồi máy từ sự cố {incident.IncidentCode}",
                    $"KTV đánh giá sự cố {incident.IncidentCode} cần mang máy về kho bảo dưỡng{codesText}. Lý do: {incident.Resolution}",
                    "INCIDENT_RETURN_REQUIRED",
                    cancellationToken: ct);
            }
        }

        var message = outcome switch
        {
            "RESOLVED" => $"Đã chốt hoàn tất xử lý sự cố {incident.IncidentCode}.",
            "REMOTE_FAILED" => $"Đã ghi nhận lý do và chuyển sự cố {incident.IncidentCode} về Quản lý để phân công lại hoặc cử đội hiện trường.",
            _ => autoReturnCodes.Count > 0
                ? $"Đã chuyển sự cố sang cần thu hồi và tự tạo phiếu thu hồi {string.Join(", ", autoReturnCodes)} chờ Kho kiểm nhận."
                : $"Đã chuyển sự cố {incident.IncidentCode} sang trạng thái cần thu hồi về kho.",
        };

        return new TechnicianResolveIncidentResponse(
            incident.Id,
            incident.IncidentCode,
            EnumName.ToDatabaseValue(incident.Status),
            autoReturnCodes,
            message);
    }

    // ================================================================ HỖ TRỢ NỘI BỘ

    private async Task<(bool IsOnSite, bool IsLeader)> ResolveTechnicianAccessAsync(
        EquipmentIncident incident,
        Guid staffId,
        CancellationToken ct)
    {
        if (incident.ServiceAssignmentId.HasValue)
        {
            var member = await db.ServiceAssignmentMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ServiceAssignmentId == incident.ServiceAssignmentId.Value && m.StaffId == staffId, ct);

            if (member is not null)
            {
                var isLeader = string.Equals(member.Role, "LEADER", StringComparison.OrdinalIgnoreCase);
                return (true, isLeader);
            }
        }

        if (incident.RemoteTechnicianStaffId == staffId)
        {
            return (false, true);
        }

        throw new AppException(
            "FORBIDDEN_INCIDENT",
            "Bạn không được phân công xử lý sự cố này.",
            StatusCodes.Status403Forbidden);
    }

    private async Task<TechnicianDepotDto> ResolveDepotAsync(CancellationToken ct)
    {
        var mainWarehouse = await db.Warehouses.AsNoTracking()
            .Where(w => w.Status == WarehouseStatus.Active)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Name, w.Address, w.Latitude, w.Longitude })
            .FirstOrDefaultAsync(ct);

        return mainWarehouse is not null
            ? new TechnicianDepotDto(
                mainWarehouse.Name,
                !string.IsNullOrWhiteSpace(mainWarehouse.Address) ? mainWarehouse.Address : _shipping.MainWarehouseAddress,
                mainWarehouse.Latitude ?? _shipping.MainWarehouseLat,
                mainWarehouse.Longitude ?? _shipping.MainWarehouseLng)
            : new TechnicianDepotDto(
                _shipping.MainWarehouseName,
                _shipping.MainWarehouseAddress,
                _shipping.MainWarehouseLat,
                _shipping.MainWarehouseLng);
    }

    private async Task<Guid> GetCurrentStaffIdAsync(Guid userId, CancellationToken ct)
    {
        var staffId = await db.Staff.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        return staffId ?? throw new AppException(
            "STAFF_PROFILE_NOT_FOUND",
            "Tài khoản chưa có hồ sơ nhân viên (Staff).",
            StatusCodes.Status403Forbidden);
    }

    private async Task EnsureValidUploadedImagesAsync(HashSet<Guid> fileIds, Guid currentUserId, CancellationToken ct)
    {
        if (fileIds.Count == 0) return;

        var files = await db.StorageFiles.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.UploadedBy, f.ContentType })
            .ToListAsync(ct);

        if (files.Count != fileIds.Count || files.Any(f => f.UploadedBy != currentUserId))
            throw new AppException("ATTACHMENT_INVALID", "Ảnh minh chứng không hợp lệ hoặc không phải do bạn tải lên.", StatusCodes.Status400BadRequest);

        if (files.Any(f => !AllowedImageFormats.Contains((f.ContentType ?? "").ToLowerInvariant())))
            throw new AppException("ATTACHMENT_FORMAT_INVALID", "Ảnh minh chứng phải có định dạng jpg, jpeg, png hoặc webp.", StatusCodes.Status400BadRequest);
    }

    private static EquipmentIncidentStatus ParseIncidentStatus(string status)
    {
        try
        {
            return EnumName.FromDatabaseValue<EquipmentIncidentStatus>(status.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new AppException("INVALID_STATUS", "Trạng thái sự cố không hợp lệ.", StatusCodes.Status400BadRequest);
        }
    }
}
