using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Finance.Services;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Configurations;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Services;

/// <summary>
/// Nghiệp vụ vận hành hiện trường cho Kỹ thuật viên (role TECHNICIAN):
/// - Tổng quan hiển thị đầy đủ số liệu & danh sách chuyến đang đi / chờ xuất phát.
/// - Upload ảnh (<=10MB) và video (<=500MB) minh chứng; riêng ảnh máy lỗi lẻ chỉ cho phép ảnh.
/// - Quét/nhập serial kiểm tra thiết bị thuộc phiếu bàn giao khi báo lỗi.
/// - Xem chi tiết đầy đủ sau bàn giao (máy đạt, chi tiết từng máy lỗi + ảnh, ảnh/video chung, phiếu thu hồi, thanh toán đợt 1).
/// </summary>
public sealed class TechnicianOperationsService(
    AppDbContext db,
    IFileUploadService uploadService,
    INotificationDispatcher notificationDispatcher,
    PaymentService paymentService,
    IOptions<ShippingOptions> shippingOptions)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly string[] AllowedImageFormats = ["jpg", "jpeg", "png", "webp"];
    private static readonly string[] AllowedVideoFormats = ["mp4", "mov", "m4v", "webm", "mkv", "avi", "3gp"];
    private const long MaxImageBytes = 10L * 1024 * 1024;
    private const long MaxVideoBytes = 500L * 1024 * 1024;

    private readonly ShippingOptions _shipping = shippingOptions.Value;

    private static FileUploadPolicy BuildEvidencePolicy(StorageFolder folder, bool isVideo) => isVideo
        ? new FileUploadPolicy
        {
            MaxFileCount = 3,
            MaxFileSizeBytes = MaxVideoBytes,
            AllowedExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".3gp"],
            ResourceType = CloudinaryResourceType.Video,
            Folder = folder,
        }
        : new FileUploadPolicy
        {
            MaxFileCount = 10,
            MaxFileSizeBytes = MaxImageBytes,
            AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
            ResourceType = CloudinaryResourceType.Image,
            Folder = folder,
        };

    // ================================================================ UPLOAD ẢNH & VIDEO MINH CHỨNG

    public Task<FileUploadTicket> CreateEvidenceUploadTicketAsync(
        FileUploadRequest request,
        string? folderName,
        string? mediaType,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var folder = (folderName ?? "handover").Trim().ToLowerInvariant() switch
        {
            "maintenance" => StorageFolder.Maintenance,
            "incident" => StorageFolder.Incident,
            _ => StorageFolder.Handover,
        };
        var isVideo = string.Equals(mediaType?.Trim(), "video", StringComparison.OrdinalIgnoreCase);
        return uploadService.CreateUploadTicketAsync(BuildEvidencePolicy(folder, isVideo), request, currentUserId, ct);
    }

    public Task<ConfirmFileUploadResponse> ConfirmEvidenceUploadAsync(
        ConfirmFileUploadRequest request,
        Guid currentUserId,
        CancellationToken ct = default)
        => uploadService.ConfirmUploadAsync(request, currentUserId, ct);

    // ================================================================ DASHBOARD

    public async Task<TechnicianDashboardResponse> GetDashboardAsync(Guid currentUserId, CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);
        var myAssignmentIds = db.ServiceAssignmentMembers.AsNoTracking()
            .Where(m => m.StaffId == staffId)
            .Select(m => m.ServiceAssignmentId);

        var pendingTrips = await db.ServiceAssignments.AsNoTracking()
            .CountAsync(a => myAssignmentIds.Contains(a.Id) && a.Status == ServiceAssignmentStatus.Pending, ct);

        var inProgressTrips = await db.ServiceAssignments.AsNoTracking()
            .CountAsync(a => myAssignmentIds.Contains(a.Id) && a.Status == ServiceAssignmentStatus.InProgress, ct);

        var vnTodayStart = new DateTimeOffset(DateTimeOffset.UtcNow.ToOffset(VietnamOffset).Date, VietnamOffset).ToUniversalTime();
        var completedToday = await db.ServiceAssignments.AsNoTracking()
            .CountAsync(a => myAssignmentIds.Contains(a.Id)
                             && a.Status == ServiceAssignmentStatus.Completed
                             && a.CompletedAt != null && a.CompletedAt >= vnTodayStart, ct);

        var pendingMaint = await db.MaintenanceTasks.AsNoTracking()
            .CountAsync(t => t.Status == MaintenanceTaskStatus.Pending
                             && (t.AssignedTechnicianStaffId == null || t.AssignedTechnicianStaffId == staffId), ct);

        var inProgressMaint = await db.MaintenanceTasks.AsNoTracking()
            .CountAsync(t => t.Status == MaintenanceTaskStatus.InProgress
                             && (t.AssignedTechnicianStaffId == null || t.AssignedTechnicianStaffId == staffId), ct);

        var openIncidents = await db.EquipmentIncidents.AsNoTracking()
            .CountAsync(i => (i.RemoteTechnicianStaffId == staffId || (i.ServiceAssignmentId != null && myAssignmentIds.Contains(i.ServiceAssignmentId.Value)))
                             && i.Status != EquipmentIncidentStatus.Resolved
                             && i.Status != EquipmentIncidentStatus.Cancelled, ct);

        // Lấy danh sách chuyến Giao lắp (HANDOVER) & Thu hồi (RETRIEVAL) đang hoạt động để hiển thị ngay trên Tổng quan
        var allMyTrips = await GetMyAssignmentsAsync(currentUserId, status: null, type: null, ct);
        var activeTrips = allMyTrips
            .Where(t => (t.Type is "HANDOVER" or "RETRIEVAL") && t.Status is "IN_PROGRESS" or "PENDING")
            .Take(8)
            .ToList();

        return new TechnicianDashboardResponse(
            pendingTrips,
            inProgressTrips,
            completedToday,
            pendingMaint,
            inProgressMaint,
            openIncidents,
            activeTrips);
    }

    // ================================================================ QUÉT / TRA SERIAL TRONG PHIẾU BÀN GIAO

    public async Task<TechnicianHandoverLookupSerialResponse> LookupHandoverSerialAsync(
        Guid handoverNoteId,
        string serial,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var note = await db.HandoverNotes.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == handoverNoteId, ct)
            ?? throw new AppException("HANDOVER_NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);

        if (note.ServiceAssignmentId.HasValue)
        {
            var inTeam = await db.ServiceAssignmentMembers.AsNoTracking()
                .AnyAsync(m => m.ServiceAssignmentId == note.ServiceAssignmentId.Value && m.StaffId == staffId, ct);
            if (!inTeam)
                throw new AppException("FORBIDDEN_ASSIGNMENT", "Bạn không thuộc chuyến giao của phiếu này.", StatusCodes.Status403Forbidden);
        }

        var normalized = (serial ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new AppException("SERIAL_REQUIRED", "Vui lòng quét hoặc nhập mã serial thiết bị.", StatusCodes.Status400BadRequest);
        }

        var match = await (
            from hi in db.HandoverItems.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on hi.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            where hi.HandoverNoteId == handoverNoteId && eq.SerialNumber.ToLower() == normalized.ToLower()
            select new { eq, m }
        ).FirstOrDefaultAsync(ct);

        if (match is null)
        {
            var existsElsewhere = await db.Equipments.AsNoTracking()
                .AnyAsync(e => e.SerialNumber.ToLower() == normalized.ToLower(), ct);

            return new TechnicianHandoverLookupSerialResponse(
                BelongsToHandover: false,
                EquipmentId: null,
                SerialNumber: normalized,
                EquipmentModelId: null,
                ModelCode: null,
                ModelName: null,
                Condition: null,
                Message: existsElsewhere
                    ? $"Mã serial \"{normalized}\" KHÔNG thuộc danh sách thiết bị của phiếu bàn giao {note.HandoverCode}."
                    : $"Không tìm thấy thiết bị nào có mã serial \"{normalized}\" trong hệ thống.");
        }

        return new TechnicianHandoverLookupSerialResponse(
            BelongsToHandover: true,
            EquipmentId: match.eq.Id,
            SerialNumber: match.eq.SerialNumber,
            EquipmentModelId: match.m.Id,
            ModelCode: match.m.Code,
            ModelName: match.m.Name,
            Condition: match.eq.Condition,
            Message: $"Hợp lệ: {match.m.Name} ({match.eq.SerialNumber}) thuộc phiếu {note.HandoverCode}.");
    }

    // ================================================================ DANH SÁCH & CHI TIẾT CHUYẾN / PHIẾU

    public async Task<List<TechnicianAssignmentSummaryResponse>> GetMyAssignmentsAsync(
        Guid currentUserId,
        string? status,
        string? type,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var myMemberships = await db.ServiceAssignmentMembers.AsNoTracking()
            .Where(m => m.StaffId == staffId)
            .ToDictionaryAsync(m => m.ServiceAssignmentId, m => m.Role, ct);

        if (myMemberships.Count == 0) return [];

        var assignmentIds = myMemberships.Keys.ToList();
        var query = db.ServiceAssignments.AsNoTracking()
            .Where(a => assignmentIds.Contains(a.Id));

        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = EnumName.FromDatabaseValue<ServiceAssignmentStatus>(status.Trim().ToUpperInvariant());
            query = query.Where(a => a.Status == parsed);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim().ToUpperInvariant();
            query = query.Where(a => a.Type == t);
        }

        var assignments = await query
            .OrderByDescending(a => a.Status == ServiceAssignmentStatus.InProgress)
            .ThenByDescending(a => a.Status == ServiceAssignmentStatus.Pending)
            .ThenBy(a => a.ScheduledStartAt)
            .ToListAsync(ct);

        if (assignments.Count == 0) return [];

        var visibleIds = assignments.Select(a => a.Id).ToList();

        var allMembers = await (
            from m in db.ServiceAssignmentMembers.AsNoTracking()
            join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where visibleIds.Contains(m.ServiceAssignmentId)
            select new
            {
                m.ServiceAssignmentId,
                Dto = new TechnicianAssignmentMemberDto(s.Id, s.EmployeeCode, u.FullName, u.Phone, m.Role)
            }
        ).ToListAsync(ct);

        var stopRows = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where h.ServiceAssignmentId != null
                  && visibleIds.Contains(h.ServiceAssignmentId.Value)
                  && h.Status != HandoverNoteStatus.Cancelled
            orderby h.StopOrder, h.CreatedAt
            select new
            {
                AssignmentId = h.ServiceAssignmentId!.Value,
                h,
                c.ContractCode,
                CompanyName = co.Name,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address
            }
        ).ToListAsync(ct);

        var handoverIds = stopRows.Select(s => s.h.Id).ToList();
        var itemCounts = handoverIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.HandoverItems.AsNoTracking()
                .Where(i => handoverIds.Contains(i.HandoverNoteId))
                .GroupBy(i => i.HandoverNoteId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var retrievalStopsByAssign = (await db.EquipmentReturns.AsNoTracking()
            .Where(r => r.ServiceAssignmentId != null
                        && visibleIds.Contains(r.ServiceAssignmentId.Value)
                        && r.Status != EquipmentReturnStatus.Cancelled)
            .Select(r => new { AssignmentId = r.ServiceAssignmentId!.Value, r.Status })
            .ToListAsync(ct))
            .GroupBy(x => x.AssignmentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return assignments.Select(a =>
        {
            var members = allMembers
                .Where(m => m.ServiceAssignmentId == a.Id)
                .Select(m => m.Dto)
                .OrderByDescending(m => m.Role == "LEADER")
                .ThenBy(m => m.FullName)
                .ToList();

            var stops = stopRows
                .Where(s => s.AssignmentId == a.Id)
                .Select(s => new TechnicianHandoverStopSummaryDto(
                    s.h.Id,
                    s.h.HandoverCode,
                    s.h.HandoverType,
                    s.h.RentalContractId,
                    s.ContractCode,
                    s.CompanyName,
                    s.Address,
                    s.h.DeliveryLatitude,
                    s.h.DeliveryLongitude,
                    s.h.ReceivedByName,
                    s.h.ReceivedByPhone,
                    s.h.StopOrder,
                    s.h.EstimatedTravelMinutes,
                    s.h.EstimatedInstallMinutes,
                    EnumName.ToDatabaseValue(s.h.Status),
                    itemCounts.GetValueOrDefault(s.h.Id, 0),
                    s.h.DeliveryAttemptNumber,
                    s.h.FailedReason,
                    s.h.FailedAt))
                .ToList();

            var isLeader = myMemberships.TryGetValue(a.Id, out var role)
                           && string.Equals(role, "LEADER", StringComparison.OrdinalIgnoreCase);

            return new TechnicianAssignmentSummaryResponse(
                a.Id,
                a.Type,
                a.Title,
                a.Description,
                EnumName.ToDatabaseValue(a.Status),
                isLeader,
                a.ScheduledStartAt,
                a.EstimatedDurationMinutes,
                a.StartedAt,
                a.CompletedAt,
                a.Type == "RETRIEVAL"
                    ? retrievalStopsByAssign.GetValueOrDefault(a.Id)?.Count ?? 0
                    : stops.Count,
                a.Type == "RETRIEVAL"
                    ? retrievalStopsByAssign.GetValueOrDefault(a.Id)?.Count(x => x.Status is EquipmentReturnStatus.Processing or EquipmentReturnStatus.Completed) ?? 0
                    : stops.Count(x => x.Status == "COMPLETED"),
                members,
                stops);
        }).ToList();
    }

    public async Task<TechnicianHandoverStopDetailDto> GetHandoverStopDetailAsync(
        Guid handoverNoteId,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var row = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where h.Id == handoverNoteId
            select new
            {
                h,
                c,
                CompanyName = co.Name,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address
            }
        ).FirstOrDefaultAsync(ct)
            ?? throw new AppException("HANDOVER_NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);

        if (row.h.ServiceAssignmentId.HasValue)
        {
            var inTeam = await db.ServiceAssignmentMembers.AsNoTracking()
                .AnyAsync(m => m.ServiceAssignmentId == row.h.ServiceAssignmentId.Value && m.StaffId == staffId, ct);
            if (!inTeam)
                throw new AppException("FORBIDDEN_ASSIGNMENT", "Bạn không thuộc chuyến công tác của phiếu này.", StatusCodes.Status403Forbidden);
        }

        var built = await BuildStopDetailsAsync(
            [(row.h, row.c, row.CompanyName, row.Address)],
            currentUserId,
            ct);

        return built.Single();
    }

    public async Task<TechnicianAssignmentDetailResponse> GetAssignmentDetailAsync(
        Guid assignmentId,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        var assignment = await db.ServiceAssignments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct)
            ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến công tác.", StatusCodes.Status404NotFound);

        var myMember = await db.ServiceAssignmentMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ServiceAssignmentId == assignmentId && m.StaffId == staffId, ct)
            ?? throw new AppException("FORBIDDEN_ASSIGNMENT", "Bạn không thuộc danh sách kỹ thuật viên của chuyến này.", StatusCodes.Status403Forbidden);

        var isLeader = string.Equals(myMember.Role, "LEADER", StringComparison.OrdinalIgnoreCase);

        var mainWarehouse = await db.Warehouses.AsNoTracking()
            .Where(w => w.Status == WarehouseStatus.Active)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Name, w.Address, w.Latitude, w.Longitude })
            .FirstOrDefaultAsync(ct);

        var depot = mainWarehouse is not null
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

        var members = await (
            from m in db.ServiceAssignmentMembers.AsNoTracking()
            join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where m.ServiceAssignmentId == assignmentId
            orderby m.Role == "LEADER" descending, u.FullName
            select new TechnicianAssignmentMemberDto(s.Id, s.EmployeeCode, u.FullName, u.Phone, m.Role)
        ).ToListAsync(ct);

        var stopRows = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where h.ServiceAssignmentId == assignmentId && h.Status != HandoverNoteStatus.Cancelled
            orderby h.StopOrder, h.CreatedAt
            select new
            {
                h,
                c,
                CompanyName = co.Name,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address
            }
        ).ToListAsync(ct);

        var stops = await BuildStopDetailsAsync(
            stopRows.Select(x => (x.h, x.c, x.CompanyName, x.Address)).ToList(),
            currentUserId,
            ct);

        var assignFiles = await (
            from att in db.ServiceAssignmentAttachments.AsNoTracking()
            join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
            where att.ServiceAssignmentId == assignmentId
            orderby att.CreatedAt
            select f
        ).ToListAsync(ct);

        var assignMedia = assignFiles.Select(BuildMediaDto).ToList();
        var assignUrls = assignMedia.Select(m => m.Url).ToList();

        List<TechnicianRetrievalStopDetailDto>? retrievalStops = null;
        if (string.Equals(assignment.Type, "RETRIEVAL", StringComparison.OrdinalIgnoreCase))
        {
            retrievalStops = await BuildRetrievalStopsAsync(assignmentId, ct);
        }

        return new TechnicianAssignmentDetailResponse(
            assignment.Id,
            assignment.Type,
            assignment.Title,
            assignment.Description,
            EnumName.ToDatabaseValue(assignment.Status),
            isLeader,
            assignment.ScheduledStartAt,
            assignment.EstimatedDurationMinutes,
            assignment.StartedAt,
            assignment.CompletedAt,
            depot,
            members,
            stops,
            assignUrls,
            retrievalStops,
            assignMedia);
    }

    private async Task<List<TechnicianHandoverStopDetailDto>> BuildStopDetailsAsync(
        List<(HandoverNote h, RentalContract c, string CompanyName, string? Address)> stopRows,
        Guid currentUserId,
        CancellationToken ct)
    {
        if (stopRows.Count == 0) return [];

        // Đảm bảo mọi hợp đồng đã ký đều có sẵn các khoản tiền thuê (RENTAL) để luôn hiển thị khối Thanh toán đợt 1 cho KTV
        foreach (var contract in stopRows.Select(x => x.c).DistinctBy(c => c.Id))
        {
            await paymentService.EnsureRentalChargesGeneratedAsync(contract, currentUserId, ct);
        }

        var handoverIds = stopRows.Select(x => x.h.Id).ToList();
        var contractIds = stopRows.Select(x => x.c.Id).Distinct().ToList();

        var handoverEquipRows = await (
            from hi in db.HandoverItems.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on hi.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            where handoverIds.Contains(hi.HandoverNoteId)
            orderby m.Name, eq.SerialNumber
            select new
            {
                hi.HandoverNoteId,
                Item = new TechnicianEquipmentItemDto(
                    eq.Id, eq.SerialNumber, m.Id, m.Code, m.Name,
                    eq.Condition, EnumName.ToDatabaseValue(eq.Status), hi.Note)
            }
        ).ToListAsync(ct);

        var noteFiles = await (
            from att in db.HandoverNoteAttachments.AsNoTracking()
            join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
            where handoverIds.Contains(att.HandoverNoteId)
            orderby att.CreatedAt
            select new { att.HandoverNoteId, File = f }
        ).ToListAsync(ct);

        // Chi tiết sự cố / máy lỗi & ảnh/video lỗi sau bàn giao
        var issues = await db.HandoverIssues.AsNoTracking()
            .Where(i => handoverIds.Contains(i.HandoverNoteId) && i.Status != HandoverIssueStatus.Cancelled)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        var issueIds = issues.Select(i => i.Id).ToList();

        var issueItemRows = issueIds.Count == 0
            ? []
            : await (
                from ii in db.HandoverIssueItems.AsNoTracking()
                join eq in db.Equipments.AsNoTracking() on ii.EquipmentId equals eq.Id
                join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
                where issueIds.Contains(ii.HandoverIssueId) && ii.Status != HandoverIssueItemStatus.Cancelled
                orderby m.Name, eq.SerialNumber
                select new { ii, eq, m }
            ).ToListAsync(ct);

        var issueFiles = issueIds.Count == 0
            ? []
            : await (
                from att in db.HandoverIssueAttachments.AsNoTracking()
                join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
                where issueIds.Contains(att.HandoverIssueId)
                orderby att.CreatedAt
                select new { att.HandoverIssueId, att.HandoverIssueItemId, File = f }
            ).ToListAsync(ct);

        // Phiếu thu hồi máy lỗi (DEFECTIVE_ON_DELIVERY) theo hợp đồng
        var defReturns = await db.EquipmentReturns.AsNoTracking()
            .Where(r => contractIds.Contains(r.RentalContractId) && r.ReturnType == "DEFECTIVE_ON_DELIVERY")
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        // Thông tin thanh toán đợt 1
        var rentalCharges = await db.RentalCharges.AsNoTracking()
            .Where(rc => rc.RentalContractId != null
                         && contractIds.Contains(rc.RentalContractId.Value)
                         && rc.ChargeType == "RENTAL"
                         && rc.Status != RentalChargeStatus.Cancelled)
            .OrderBy(rc => rc.PeriodStart)
            .ThenBy(rc => rc.CreatedAt)
            .ToListAsync(ct);

        var pendingCashChargeIds = await db.PaymentTransactions.AsNoTracking()
            .Where(t => contractIds.Contains(t.RentalContractId)
                        && t.PaymentMethod == PaymentMethod.Cash
                        && t.Status == PaymentTransactionStatus.Pending
                        && t.RentalChargeId != null)
            .Select(t => t.RentalChargeId!.Value)
            .ToListAsync(ct);
        var pendingCashSet = pendingCashChargeIds.ToHashSet();

        var shippingFeeByContract = await db.RentalContractServices.AsNoTracking()
            .Where(s => contractIds.Contains(s.RentalContractId) && s.ProvidedBy == "RENTAL_COMPANY")
            .GroupBy(s => s.RentalContractId)
            .Select(g => new { g.Key, Sum = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

        return stopRows.Select(s =>
        {
            var items = handoverEquipRows
                .Where(x => x.HandoverNoteId == s.h.Id)
                .Select(x => x.Item)
                .ToList();

            var groups = items
                .GroupBy(x => new { x.EquipmentModelId, x.ModelCode, x.ModelName })
                .Select(g => new TechnicianEquipmentGroupDto(
                    g.Key.EquipmentModelId,
                    g.Key.ModelCode,
                    g.Key.ModelName,
                    g.Count(),
                    g.ToList()))
                .ToList();

            var sceneMedia = noteFiles
                .Where(x => x.HandoverNoteId == s.h.Id)
                .Select(x => BuildMediaDto(x.File))
                .ToList();

            var firstCharge = rentalCharges.FirstOrDefault(rc => rc.RentalContractId == s.c.Id);
            TechnicianFirstPaymentInfoDto? firstPayment = null;
            if (firstCharge != null)
            {
                var remaining = Math.Max(0m, firstCharge.Amount - firstCharge.PaidAmount);
                firstPayment = new TechnicianFirstPaymentInfoDto(
                    s.c.Id,
                    s.c.RentalType,
                    firstCharge.Id,
                    firstCharge.Description ?? "Tiền thuê đợt 1",
                    firstCharge.Amount,
                    firstCharge.PaidAmount,
                    remaining,
                    EnumName.ToDatabaseValue(firstCharge.Status),
                    pendingCashSet.Contains(firstCharge.Id),
                    shippingFeeByContract.GetValueOrDefault(s.c.Id, 0m));
            }

            TechnicianHandoverResultDetailDto? deliveryResult = null;
            if (s.h.Status == HandoverNoteStatus.Completed)
            {
                var noteIssue = issues.FirstOrDefault(i => i.HandoverNoteId == s.h.Id);
                var noteIssueItems = noteIssue is null
                    ? []
                    : issueItemRows.Where(x => x.ii.HandoverIssueId == noteIssue.Id).ToList();

                var defEqIds = noteIssueItems.Select(x => x.eq.Id).ToHashSet();
                var goodItems = items.Where(i => !defEqIds.Contains(i.EquipmentId)).ToList();

                var sharedIssueMedia = noteIssue is null
                    ? new List<TechnicianMediaAttachmentDto>()
                    : issueFiles
                        .Where(x => x.HandoverIssueId == noteIssue.Id && x.HandoverIssueItemId == null)
                        .Select(x => BuildMediaDto(x.File))
                        .ToList();

                var defDtos = noteIssueItems.Select(x =>
                {
                    var itemPhotos = issueFiles
                        .Where(f => f.HandoverIssueId == x.ii.HandoverIssueId && f.HandoverIssueItemId == x.ii.Id)
                        .Select(f => BuildMediaDto(f.File))
                        .ToList();

                    return new TechnicianDefectiveIssueItemDetailDto(
                        x.ii.Id,
                        x.eq.Id,
                        x.eq.SerialNumber,
                        x.m.Code,
                        x.m.Name,
                        x.ii.IssueType,
                        x.ii.Description,
                        EnumName.ToDatabaseValue(x.ii.Status),
                        itemPhotos);
                }).ToList();

                var matchedRet = defEqIds.Count > 0
                    ? defReturns.FirstOrDefault(r =>
                        r.RentalContractId == s.c.Id &&
                        (r.Note?.Contains(s.h.HandoverCode, StringComparison.OrdinalIgnoreCase) ?? false))
                      ?? defReturns.FirstOrDefault(r => r.RentalContractId == s.c.Id)
                    : null;

                deliveryResult = new TechnicianHandoverResultDetailDto(
                    goodItems.Count,
                    defDtos.Count,
                    noteIssue?.Description,
                    matchedRet?.Id,
                    matchedRet?.ReturnCode,
                    matchedRet is null ? null : EnumName.ToDatabaseValue(matchedRet.Status),
                    sceneMedia,
                    sharedIssueMedia,
                    goodItems,
                    defDtos);
            }

            return new TechnicianHandoverStopDetailDto(
                s.h.Id,
                s.h.HandoverCode,
                s.h.HandoverType,
                s.c.Id,
                s.c.ContractCode,
                EnumName.ToDatabaseValue(s.c.Status),
                s.CompanyName,
                s.Address,
                s.h.DeliveryLatitude,
                s.h.DeliveryLongitude,
                s.c.DeliveryNote,
                s.h.ReceivedByName,
                s.h.ReceivedByPhone,
                s.h.StopOrder,
                s.h.EstimatedTravelMinutes,
                s.h.EstimatedInstallMinutes,
                EnumName.ToDatabaseValue(s.h.Status),
                s.h.DeliveryAttemptNumber,
                s.h.FailedReason,
                s.h.FailedAt,
                s.h.Note,
                groups,
                sceneMedia.Select(m => m.Url).ToList(),
                firstPayment,
                sceneMedia,
                deliveryResult);
        }).ToList();
    }

    // ================================================================ BẮT ĐẦU XUẤT PHÁT (CHỈ KTV TRƯỞNG)

    public async Task<TechnicianAssignmentDetailResponse> StartAssignmentAsync(
        Guid assignmentId,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);
        await EnsureLeaderOfAssignmentAsync(assignmentId, staffId, ct);

        var assignment = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct)
            ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến công tác.", StatusCodes.Status404NotFound);

        if (assignment.Status != ServiceAssignmentStatus.Pending)
            throw new AppException("INVALID_STATUS", "Chỉ có thể bắt đầu chuyến đang ở trạng thái chờ xuất phát (PENDING).", StatusCodes.Status409Conflict);

        if (assignment.Type == "HANDOVER")
        {
            var notes = await db.HandoverNotes.AsNoTracking()
                .Where(h => h.ServiceAssignmentId == assignmentId && h.Status != HandoverNoteStatus.Cancelled)
                .ToListAsync(ct);

            if (notes.Count == 0)
                throw new AppException("NO_HANDOVER_STOPS", "Chuyến giao chưa có điểm bàn giao nào.", StatusCodes.Status409Conflict);

            var unapproved = notes.Where(n => n.Status != HandoverNoteStatus.Shipping && n.Status != HandoverNoteStatus.Completed).ToList();
            if (unapproved.Count > 0)
            {
                var codes = string.Join(", ", unapproved.Select(n => n.HandoverCode));
                throw new AppException(
                    "HANDOVER_NOT_APPROVED_FOR_SHIPPING",
                    $"Chưa thể xuất phát vì các phiếu sau chưa được Kho soạn xong & Manager duyệt xuất kho: {codes}.",
                    StatusCodes.Status409Conflict);
            }
        }

        var now = DateTimeOffset.UtcNow;
        assignment.Status = ServiceAssignmentStatus.InProgress;
        assignment.StartedAt = now;
        assignment.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return await GetAssignmentDetailAsync(assignmentId, currentUserId, ct);
    }

    // ================================================================ HOÀN TẤT BÀN GIAO TẠI HIỆN TRƯỜNG (CHỈ KTV TRƯỞNG)

    public async Task<TechnicianCompleteHandoverResponse> CompleteHandoverAsync(
        Guid handoverNoteId,
        TechnicianCompleteHandoverRequest request,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var note = await db.HandoverNotes.FirstOrDefaultAsync(n => n.Id == handoverNoteId, ct)
            ?? throw new AppException("HANDOVER_NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);

        if (!note.ServiceAssignmentId.HasValue)
            throw new AppException("NO_ASSIGNMENT", "Phiếu bàn giao này chưa được gán vào chuyến đi nào.", StatusCodes.Status409Conflict);

        await EnsureLeaderOfAssignmentAsync(note.ServiceAssignmentId.Value, staffId, ct);

        var assignment = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == note.ServiceAssignmentId.Value, ct)
            ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến giao hàng.", StatusCodes.Status404NotFound);

        if (assignment.Status != ServiceAssignmentStatus.InProgress)
            throw new AppException("ASSIGNMENT_NOT_STARTED", "Vui lòng bấm \"Bắt đầu xuất phát\" cho chuyến đi trước khi xác nhận bàn giao.", StatusCodes.Status409Conflict);

        if (note.Status != HandoverNoteStatus.Shipping)
            throw new AppException("INVALID_STATUS", "Chỉ có thể hoàn tất phiếu bàn giao đang ở trạng thái Đang giao (SHIPPING).", StatusCodes.Status409Conflict);

        // Kiểm tra tệp minh chứng:
        // - Hiện trường & đợt lỗi chung: cho phép Ảnh hoặc Video (<= 500MB)
        // - Từng máy lỗi riêng lẻ: CHỈ CHO PHÉP ẢNH
        await EnsureValidUploadedMediaAsync(request.SceneAttachmentFileIds.ToHashSet(), currentUserId, allowVideo: true, ct);
        await EnsureValidUploadedMediaAsync(request.IssueAttachmentFileIds.ToHashSet(), currentUserId, allowVideo: true, ct);

        var individualItemFileIds = request.DefectiveItems
            .SelectMany(d => d.AttachmentFileIds)
            .ToHashSet();
        await EnsureValidUploadedMediaAsync(individualItemFileIds, currentUserId, allowVideo: false, ct);

        if (request.DefectiveItems.Count > 0)
        {
            var hasAnyIssueMedia = request.IssueAttachmentFileIds.Count > 0
                                   || request.DefectiveItems.Any(d => d.AttachmentFileIds.Count > 0);
            if (!hasAnyIssueMedia)
            {
                throw new AppException(
                    "ISSUE_MEDIA_REQUIRED",
                    "Khi ghi nhận máy lỗi lúc giao/lắp, vui lòng đính kèm ít nhất 1 ảnh/video chung cho đợt lỗi (hoặc ảnh riêng từng máy lỗi).",
                    StatusCodes.Status400BadRequest);
            }
        }

        var contract = await db.RentalContracts
            .FromSqlInterpolated($"SELECT * FROM rental_contracts WHERE id = {note.RentalContractId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng thuê.", StatusCodes.Status404NotFound);

        var handoverItems = await db.HandoverItems
            .Where(i => i.HandoverNoteId == note.Id)
            .ToListAsync(ct);

        var handoverEqIds = handoverItems.Select(i => i.EquipmentId).ToHashSet();
        var defectiveByEqId = new Dictionary<Guid, TechnicianDefectiveItemRequest>();
        foreach (var d in request.DefectiveItems)
        {
            var eqId = d.EquipmentId!.Value;
            if (!handoverEqIds.Contains(eqId))
                throw new AppException("EQUIPMENT_NOT_IN_HANDOVER", "Có thiết bị báo lỗi không thuộc phiếu bàn giao này.", StatusCodes.Status400BadRequest);
            defectiveByEqId[eqId] = d;
        }

        var now = DateTimeOffset.UtcNow;

        var existingNoteAttIds = (await db.HandoverNoteAttachments
            .Where(a => a.HandoverNoteId == note.Id)
            .Select(a => a.StorageFileId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var fileId in request.SceneAttachmentFileIds.Distinct())
        {
            if (existingNoteAttIds.Add(fileId))
            {
                db.HandoverNoteAttachments.Add(new HandoverNoteAttachment
                {
                    Id = Guid.CreateVersion7(),
                    HandoverNoteId = note.Id,
                    StorageFileId = fileId,
                    CreatedAt = now,
                });
            }
        }

        var equipments = await db.Equipments
            .Where(e => handoverEqIds.Contains(e.Id))
            .ToListAsync(ct);

        var goodCount = 0;
        foreach (var eq in equipments)
        {
            if (defectiveByEqId.ContainsKey(eq.Id))
            {
                eq.Condition = "DAMAGED";
                eq.UpdatedAt = now;
            }
            else
            {
                eq.Status = EquipmentStatus.Rented;
                eq.UpdatedAt = now;
                goodCount++;
            }
        }

        Guid? autoReturnId = null;
        string? autoReturnCode = null;

        if (defectiveByEqId.Count > 0)
        {
            var issue = new HandoverIssue
            {
                Id = Guid.CreateVersion7(),
                HandoverNoteId = note.Id,
                Description = string.IsNullOrWhiteSpace(request.Note)
                    ? $"Phát hiện {defectiveByEqId.Count} thiết bị lỗi khi giao/lắp đặt ({note.HandoverCode})."
                    : request.Note.Trim(),
                Status = HandoverIssueStatus.Open,
                CreatedBy = currentUserId,
                CreatedAt = now,
            };
            db.HandoverIssues.Add(issue);

            var addedIssueFileIds = new HashSet<Guid>();

            foreach (var fileId in request.IssueAttachmentFileIds.Distinct())
            {
                if (addedIssueFileIds.Add(fileId))
                {
                    db.HandoverIssueAttachments.Add(new HandoverIssueAttachment
                    {
                        Id = Guid.CreateVersion7(),
                        HandoverIssueId = issue.Id,
                        HandoverIssueItemId = null,
                        StorageFileId = fileId,
                        CreatedAt = now,
                    });
                }
            }

            foreach (var (eqId, d) in defectiveByEqId)
            {
                var issueItem = new HandoverIssueItem
                {
                    Id = Guid.CreateVersion7(),
                    HandoverIssueId = issue.Id,
                    EquipmentId = eqId,
                    IssueType = NormalizeIssueType(d.IssueType),
                    Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim(),
                    Status = HandoverIssueItemStatus.Open,
                };
                db.HandoverIssueItems.Add(issueItem);

                foreach (var fileId in d.AttachmentFileIds.Distinct())
                {
                    if (addedIssueFileIds.Add(fileId))
                    {
                        db.HandoverIssueAttachments.Add(new HandoverIssueAttachment
                        {
                            Id = Guid.CreateVersion7(),
                            HandoverIssueId = issue.Id,
                            HandoverIssueItemId = issueItem.Id,
                            StorageFileId = fileId,
                            CreatedAt = now,
                        });
                    }
                }
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

            var retReceipt = new EquipmentReturn
            {
                Id = Guid.CreateVersion7(),
                ReturnCode = $"{returnPrefix}{maxRet + 1:D4}",
                RentalContractId = contract.Id,
                ReturnedAt = now,
                ReceivedBy = currentUserId,
                AssignedStorekeeperId = null, // Chưa có Thủ kho tiếp nhận -> hiển thị cho tất cả Thủ kho để nhận trước
                Status = EquipmentReturnStatus.Processing,
                ReturnType = "DEFECTIVE_ON_DELIVERY",
                Note = $"Thu hồi tự động {defectiveByEqId.Count} máy lỗi khi giao phiếu {note.HandoverCode}. Phải thu hồi xong trước khi lập phiếu & xếp lịch giao bù.",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.EquipmentReturns.Add(retReceipt);
            autoReturnId = retReceipt.Id;
            autoReturnCode = retReceipt.ReturnCode;

            foreach (var (eqId, d) in defectiveByEqId)
            {
                db.EquipmentReturnItems.Add(new EquipmentReturnItem
                {
                    Id = Guid.CreateVersion7(),
                    EquipmentReturnId = retReceipt.Id,
                    EquipmentId = eqId,
                    Status = EquipmentReturnItemStatus.Returned,
                    Note = string.IsNullOrWhiteSpace(d.Description) ? "Lỗi khi giao/lắp đặt" : d.Description.Trim(),
                });
            }
        }

        note.Status = HandoverNoteStatus.Completed;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            note.Note = string.IsNullOrWhiteSpace(note.Note)
                ? request.Note.Trim()
                : $"{note.Note}\n[KTV bàn giao]: {request.Note.Trim()}";
        }
        note.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        var totalRequired = await db.RentalContractItems.AsNoTracking()
            .Where(i => i.RentalContractId == contract.Id)
            .SumAsync(i => i.Quantity, ct);

        var defectivePairs = await (
            from iss in db.HandoverIssues.AsNoTracking()
            join issItem in db.HandoverIssueItems.AsNoTracking() on iss.Id equals issItem.HandoverIssueId
            join hn in db.HandoverNotes.AsNoTracking() on iss.HandoverNoteId equals hn.Id
            where hn.RentalContractId == contract.Id
                  && iss.Status != HandoverIssueStatus.Cancelled
                  && issItem.Status != HandoverIssueItemStatus.Cancelled
            select new { hn.Id, issItem.EquipmentId }
        ).ToListAsync(ct);
        var defectiveSet = defectivePairs.Select(x => (x.Id, x.EquipmentId)).ToHashSet();

        var completedItems = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            where hn.RentalContractId == contract.Id && hn.Status == HandoverNoteStatus.Completed
            select new { hn.Id, hi.EquipmentId }
        ).ToListAsync(ct);

        var totalDeliveredGood = completedItems.Count(x => !defectiveSet.Contains((x.Id, x.EquipmentId)));

        if (defectiveByEqId.Count > 0 || totalDeliveredGood < totalRequired)
        {
            if (contract.Status != RentalContractStatus.PartiallyDelivered)
            {
                ChangeContractStatus(contract, RentalContractStatus.PartiallyDelivered, currentUserId,
                    $"Hoàn tất giao {note.HandoverCode}: {totalDeliveredGood}/{totalRequired} máy đạt, cần thu hồi {defectiveByEqId.Count} máy lỗi và giao bù {Math.Max(0, totalRequired - totalDeliveredGood)} máy.");
            }
        }
        else if (contract.Status != RentalContractStatus.Delivered)
        {
            ChangeContractStatus(contract, RentalContractStatus.Delivered, currentUserId,
                $"Đã bàn giao đủ {totalRequired} thiết bị qua phiếu {note.HandoverCode}.");
        }

        // Nếu đây là phiếu giao máy thay thế sự cố (INCIDENT_REPLACEMENT) và giao đạt hết -> tự động đóng sự cố sang RESOLVED
        string? resolvedIncidentCode = null;
        if (string.Equals(note.HandoverType, "INCIDENT_REPLACEMENT", StringComparison.OrdinalIgnoreCase)
            && note.IncidentId.HasValue
            && defectiveByEqId.Count == 0)
        {
            var inc = await db.EquipmentIncidents.FirstOrDefaultAsync(i => i.Id == note.IncidentId.Value, ct);
            if (inc is not null && inc.Status == EquipmentIncidentStatus.ReturnRequired)
            {
                inc.Status = EquipmentIncidentStatus.Resolved;
                inc.ResolvedAt = now;
                inc.Resolution = string.IsNullOrWhiteSpace(inc.Resolution)
                    ? $"Đã hoàn tất giao thiết bị thay thế qua phiếu {note.HandoverCode}."
                    : $"{inc.Resolution} [Giao thay thế xong]: Đã bàn giao máy thay thế qua phiếu {note.HandoverCode}.";
                inc.UpdatedAt = now;
                resolvedIncidentCode = inc.IncidentCode;

                var incItemsToLink = await db.EquipmentIncidentItems
                    .Where(ii => ii.IncidentId == inc.Id && ii.ReplacementHandoverNoteId == null)
                    .ToListAsync(ct);
                foreach (var ii in incItemsToLink)
                    ii.ReplacementHandoverNoteId = note.Id;
            }
        }

        await CloseAssignmentIfNoRemainingStopsAsync(assignment, note.Id, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (resolvedIncidentCode is not null)
        {
            await notificationDispatcher.NotifyCompanyAsync(
                contract.CompanyId,
                $"Đã bàn giao máy thay thế — Hoàn tất sự cố ({resolvedIncidentCode})",
                $"Đội kỹ thuật đã bàn giao xong thiết bị thay thế qua phiếu {note.HandoverCode}. Sự cố {resolvedIncidentCode} đã được đóng hoàn tất (RESOLVED).",
                "INCIDENT_RESOLVED",
                cancellationToken: ct);

            if (contract.ApprovedBy.HasValue)
            {
                await notificationDispatcher.NotifyUsersAsync(
                    [contract.ApprovedBy.Value],
                    $"Đã giao máy thay thế xong ({resolvedIncidentCode})",
                    $"Phiếu {note.HandoverCode} đã bàn giao thành công {goodCount} thiết bị thay thế. Sự cố {resolvedIncidentCode} đã tự động chuyển RESOLVED.",
                    "INCIDENT_RESOLVED",
                    cancellationToken: ct);
            }
        }

        if (defectiveByEqId.Count > 0 && contract.ApprovedBy.HasValue)
        {
            await notificationDispatcher.NotifyUsersAsync(
                [contract.ApprovedBy.Value],
                $"Giao thiếu do máy lỗi — Chờ thu hồi ({contract.ContractCode})",
                $"Phiếu {note.HandoverCode} đã bàn giao {goodCount} máy đạt và phát hiện {defectiveByEqId.Count} máy lỗi. Hệ thống đã tự tạo phiếu thu hồi {autoReturnCode}. Sau khi Kho kiểm nhận thu hồi xong, bạn có thể tạo lịch giao bù miễn phí ship.",
                "HANDOVER_DEFECTIVE",
                cancellationToken: ct);
        }

        return new TechnicianCompleteHandoverResponse(
            note.Id,
            note.HandoverCode,
            EnumName.ToDatabaseValue(contract.Status),
            goodCount,
            defectiveByEqId.Count,
            autoReturnId,
            autoReturnCode,
            defectiveByEqId.Count > 0
                ? $"Đã bàn giao {goodCount} máy đạt. Đã tạo phiếu thu hồi {autoReturnCode} cho {defectiveByEqId.Count} máy lỗi (thu hồi xong sẽ mở giao bù)."
                : $"Đã hoàn tất bàn giao đủ {goodCount} máy tại hiện trường!");
    }

    // ================================================================ BÁO GIAO KHÔNG THÀNH CÔNG (CHỈ KTV TRƯỞNG)

    public async Task<TechnicianFailHandoverResponse> FailHandoverAsync(
        Guid handoverNoteId,
        TechnicianFailHandoverRequest request,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var note = await db.HandoverNotes.FirstOrDefaultAsync(n => n.Id == handoverNoteId, ct)
            ?? throw new AppException("HANDOVER_NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);

        if (!note.ServiceAssignmentId.HasValue)
            throw new AppException("NO_ASSIGNMENT", "Phiếu bàn giao này chưa gắn vào chuyến giao nào.", StatusCodes.Status409Conflict);

        await EnsureLeaderOfAssignmentAsync(note.ServiceAssignmentId.Value, staffId, ct);

        var assignment = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == note.ServiceAssignmentId.Value, ct)
            ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến giao hàng.", StatusCodes.Status404NotFound);

        if (assignment.Status != ServiceAssignmentStatus.InProgress)
            throw new AppException("ASSIGNMENT_NOT_STARTED", "Vui lòng bấm \"Bắt đầu xuất phát\" trước khi báo giao không thành công.", StatusCodes.Status409Conflict);

        if (note.Status != HandoverNoteStatus.Shipping)
            throw new AppException("INVALID_STATUS", "Chỉ ghi nhận thất bại cho phiếu đang ở trạng thái Đang giao (SHIPPING).", StatusCodes.Status409Conflict);

        // Cho phép cả Ảnh & Video (<= 500MB) làm minh chứng giao thất bại
        await EnsureValidUploadedMediaAsync(request.AttachmentFileIds.ToHashSet(), currentUserId, allowVideo: true, ct);

        var contract = await db.RentalContracts.FirstOrDefaultAsync(c => c.Id == note.RentalContractId, ct)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng thuê.", StatusCodes.Status404NotFound);

        var now = DateTimeOffset.UtcNow;
        var reason = request.Reason.Trim();

        var existingNoteAtt = (await db.HandoverNoteAttachments
            .Where(a => a.HandoverNoteId == note.Id)
            .Select(a => a.StorageFileId)
            .ToListAsync(ct)).ToHashSet();

        var existingAssignAtt = (await db.ServiceAssignmentAttachments
            .Where(a => a.ServiceAssignmentId == assignment.Id)
            .Select(a => a.StorageFileId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var fileId in request.AttachmentFileIds.Distinct())
        {
            if (existingNoteAtt.Add(fileId))
            {
                db.HandoverNoteAttachments.Add(new HandoverNoteAttachment
                {
                    Id = Guid.CreateVersion7(),
                    HandoverNoteId = note.Id,
                    StorageFileId = fileId,
                    CreatedAt = now,
                });
            }
            if (existingAssignAtt.Add(fileId))
            {
                db.ServiceAssignmentAttachments.Add(new ServiceAssignmentAttachment
                {
                    Id = Guid.CreateVersion7(),
                    ServiceAssignmentId = assignment.Id,
                    StorageFileId = fileId,
                    CreatedAt = now,
                });
            }
        }

        var isSupplementary = string.Equals(note.HandoverType, "SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase);
        var penaltyApplied = false;
        var penaltyAmount = 0m;

        if (!isSupplementary)
        {
            note.DeliveryAttemptNumber = note.FailedAt.HasValue
                ? Math.Max(1, note.DeliveryAttemptNumber) + 1
                : Math.Max(1, note.DeliveryAttemptNumber);
        }

        note.FailedReason = reason;
        note.FailedAt = now;
        note.Note = string.IsNullOrWhiteSpace(note.Note)
            ? $"[Giao thất bại]: {reason}"
            : $"{note.Note}\n[Giao thất bại{(isSupplementary ? " (phiếu bù)" : $" lần {note.DeliveryAttemptNumber}")}]: {reason}";

        note.ServiceAssignmentId = null;
        note.Status = HandoverNoteStatus.Preparing;
        note.UpdatedAt = now;

        if (!isSupplementary && note.DeliveryAttemptNumber > 3)
        {
            var shippingFee = await db.RentalContractServices.AsNoTracking()
                .Where(s => s.RentalContractId == contract.Id && s.ProvidedBy == "RENTAL_COMPANY")
                .SumAsync(s => (decimal?)s.Amount, ct) ?? 0m;

            penaltyAmount = shippingFee > 0 ? shippingFee : 200_000m;
            penaltyApplied = true;

            db.RentalCharges.Add(new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "CONTRACT",
                RentalContractId = contract.Id,
                ChargeType = "TRANSPORT",
                Description = $"Phí phạt giao hàng thất bại lần {note.DeliveryAttemptNumber} (vượt quá 3 lần du di) - {contract.ContractCode}: {reason}",
                Quantity = 1,
                UnitPrice = penaltyAmount,
                Amount = penaltyAmount,
                DueDate = DateOnly.FromDateTime(now.ToOffset(VietnamOffset).Date.AddDays(3)),
                PaidAmount = 0,
                Status = RentalChargeStatus.Unpaid,
                CreatedBy = currentUserId,
                CreatedAt = now,
                Note = $"KTV trưởng ghi nhận giao thất bại lần {note.DeliveryAttemptNumber} phiếu {note.HandoverCode}.",
            });
        }

        await db.SaveChangesAsync(ct);
        await CloseAssignmentIfNoRemainingStopsAsync(assignment, note.Id, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (contract.ApprovedBy.HasValue)
        {
            await notificationDispatcher.NotifyUsersAsync(
                [contract.ApprovedBy.Value],
                $"Giao hàng thất bại — Cần xếp lịch lại ({contract.ContractCode})",
                $"Phiếu {note.HandoverCode} giao không thành công{(isSupplementary ? " (phiếu giao bù)" : $" (lần {note.DeliveryAttemptNumber})")}. Lý do: {reason}. Phiếu đã được chuyển về danh sách chờ điều phối kèm ảnh/video minh chứng.",
                "HANDOVER_FAILED",
                cancellationToken: ct);
        }

        if (penaltyApplied)
        {
            var repEmail = await db.RentalContractRepresentatives.AsNoTracking()
                .Where(r => r.RentalContractId == contract.Id && r.Email != null)
                .Select(r => r.Email!)
                .FirstOrDefaultAsync(ct);

            var msg = $"Đơn giao hàng {note.HandoverCode} (Hợp đồng {contract.ContractCode}) giao không thành công lần thứ {note.DeliveryAttemptNumber} (vượt quá 3 lần miễn phạt). Lý do: {reason}. Hệ thống đã lập khoản phụ phí giao lại {penaltyAmount:N0} đ.";
            await notificationDispatcher.NotifyCompanyAsync(
                contract.CompanyId,
                $"Phí phạt giao hàng thất bại ({contract.ContractCode})",
                msg,
                "DELIVERY_FAILED_PENALTY",
                extraEmails: repEmail is not null ? [repEmail] : null,
                cancellationToken: ct);
        }

        return new TechnicianFailHandoverResponse(
            note.Id,
            note.HandoverCode,
            isSupplementary ? 0 : note.DeliveryAttemptNumber,
            penaltyApplied,
            penaltyAmount,
            penaltyApplied
                ? $"Đã ghi nhận giao thất bại lần {note.DeliveryAttemptNumber}, lập khoản phạt {penaltyAmount:N0} đ và báo Quản lý xếp lịch lại."
                : "Đã ghi nhận giao không thành công kèm minh chứng và chuyển về Quản lý xếp lịch giao mới.");
    }

    // ================================================================ CHUYẾN THU HỒI THIẾT BỊ (RETRIEVAL)

    private async Task<List<TechnicianRetrievalStopDetailDto>> BuildRetrievalStopsAsync(
        Guid assignmentId,
        CancellationToken ct)
    {
        var retRows = await (
            from r in db.EquipmentReturns.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on r.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where r.ServiceAssignmentId == assignmentId && r.Status != EquipmentReturnStatus.Cancelled
            orderby r.StopOrder, r.CreatedAt
            select new
            {
                r,
                c,
                CompanyName = co.Name,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address
            }
        ).ToListAsync(ct);

        if (retRows.Count == 0) return [];

        var retIds = retRows.Select(x => x.r.Id).ToList();
        var contractIds = retRows.Select(x => x.c.Id).Distinct().ToList();

        var reps = (await db.RentalContractRepresentatives.AsNoTracking()
            .Where(rep => contractIds.Contains(rep.RentalContractId))
            .ToListAsync(ct))
            .GroupBy(rep => rep.RentalContractId)
            .ToDictionary(g => g.Key, g => g.First());

        var itemRows = await (
            from ri in db.EquipmentReturnItems.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on ri.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            where retIds.Contains(ri.EquipmentReturnId)
            orderby m.Name, eq.SerialNumber
            select new { ri, eq, m }
        ).ToListAsync(ct);

        return retRows.Select(x =>
        {
            reps.TryGetValue(x.c.Id, out var rep);
            var items = itemRows
                .Where(i => i.ri.EquipmentReturnId == x.r.Id)
                .Select(i => new TechnicianRetrievalItemDto(
                    i.eq.Id,
                    i.eq.SerialNumber,
                    i.m.Id,
                    i.m.Code,
                    i.m.Name,
                    i.eq.Condition,
                    EnumName.ToDatabaseValue(i.ri.Status),
                    i.ri.Note))
                .ToList();

            return new TechnicianRetrievalStopDetailDto(
                x.r.Id,
                x.r.ReturnCode,
                x.c.Id,
                x.c.ContractCode,
                EnumName.ToDatabaseValue(x.c.Status),
                x.CompanyName,
                x.Address,
                x.r.PickupLatitude,
                x.r.PickupLongitude,
                rep?.FullName,
                rep?.Phone,
                x.c.EndDate,
                x.r.StopOrder,
                x.r.EstimatedTravelMinutes,
                x.r.EstimatedUninstallMinutes,
                EnumName.ToDatabaseValue(x.r.Status),
                x.r.Note,
                items);
        }).ToList();
    }

    /// <summary>
    /// Chỉ KTV trưởng: Hoàn tất thu hồi máy tại điểm dừng (chuyến RETRIEVAL đang IN_PROGRESS, phiếu DRAFT → PROCESSING)
    /// kèm kiểm đếm từng serial + ảnh/video minh chứng hiện trường để mang về kho cho Thủ kho kiểm nhận.
    /// </summary>
    public async Task<TechnicianCompleteRetrievalResponse> CompleteRetrievalStopAsync(
        Guid returnId,
        TechnicianCompleteRetrievalRequest request,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ret = await db.EquipmentReturns
            .FromSqlInterpolated($"SELECT * FROM equipment_returns WHERE id = {returnId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new AppException("RETURN_NOT_FOUND", "Không tìm thấy phiếu thu hồi.", StatusCodes.Status404NotFound);

        if (!ret.ServiceAssignmentId.HasValue)
            throw new AppException("NO_ASSIGNMENT", "Phiếu thu hồi này không thuộc chuyến thu hồi KTV.", StatusCodes.Status409Conflict);

        await EnsureLeaderOfAssignmentAsync(ret.ServiceAssignmentId.Value, staffId, ct);

        var assignment = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == ret.ServiceAssignmentId.Value, ct)
            ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến thu hồi.", StatusCodes.Status404NotFound);

        if (assignment.Status != ServiceAssignmentStatus.InProgress)
            throw new AppException("ASSIGNMENT_NOT_STARTED", "Vui lòng bấm \"Bắt đầu xuất phát\" trước khi xác nhận thu hồi tại điểm dừng.", StatusCodes.Status409Conflict);

        if (ret.Status != EquipmentReturnStatus.Draft)
            throw new AppException("INVALID_STATUS", "Điểm thu hồi này đã được xác nhận trước đó.", StatusCodes.Status409Conflict);

        await EnsureValidUploadedMediaAsync(request.AttachmentFileIds.ToHashSet(), currentUserId, allowVideo: true, ct);

        var existingItems = await db.EquipmentReturnItems
            .Where(i => i.EquipmentReturnId == ret.Id)
            .ToListAsync(ct);

        var expectedIds = existingItems.Select(i => i.EquipmentId).ToHashSet();
        var submittedMap = request.Items
            .Where(i => i.EquipmentId.HasValue)
            .GroupBy(i => i.EquipmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Last());

        if (!expectedIds.SetEquals(submittedMap.Keys))
        {
            throw new AppException(
                "INCOMPLETE_RETRIEVAL_ITEMS",
                "Vui lòng kiểm đếm xác nhận đủ toàn bộ thiết bị trên phiếu thu hồi (RETURNED hoặc MISSING).",
                StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;
        var eqMap = await db.Equipments
            .Where(e => expectedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        var returnedCount = 0;
        var missingCount = 0;

        foreach (var itemEntity in existingItems)
        {
            var line = submittedMap[itemEntity.EquipmentId];
            var st = line.Status.Trim().ToUpperInvariant() switch
            {
                "RETURNED" => EquipmentReturnItemStatus.Returned,
                "MISSING" => EquipmentReturnItemStatus.Missing,
                _ => throw new AppException("INVALID_STATUS", "Trạng thái kiểm máy phải là RETURNED hoặc MISSING.", StatusCodes.Status400BadRequest),
            };

            itemEntity.Status = st;
            var condTag = string.IsNullOrWhiteSpace(line.Condition) ? null : line.Condition.Trim().ToUpperInvariant();
            if (st == EquipmentReturnItemStatus.Returned)
            {
                returnedCount++;
                if (condTag is "GOOD" or "FAIR" or "DAMAGED")
                {
                    eqMap[itemEntity.EquipmentId].Condition = condTag;
                    eqMap[itemEntity.EquipmentId].UpdatedAt = now;
                }
            }
            else
            {
                missingCount++;
            }

            var noteParts = new List<string>();
            if (condTag is not null && st == EquipmentReturnItemStatus.Returned)
                noteParts.Add($"Tình trạng hiện trường: {condTag}");
            if (!string.IsNullOrWhiteSpace(line.Note))
                noteParts.Add(line.Note.Trim());
            itemEntity.Note = noteParts.Count > 0 ? string.Join(" | ", noteParts) : itemEntity.Note;
        }

        var existingAttIds = (await db.ServiceAssignmentAttachments
            .Where(a => a.ServiceAssignmentId == assignment.Id)
            .Select(a => a.StorageFileId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var fileId in request.AttachmentFileIds.Distinct())
        {
            if (existingAttIds.Add(fileId))
            {
                db.ServiceAssignmentAttachments.Add(new ServiceAssignmentAttachment
                {
                    Id = Guid.CreateVersion7(),
                    ServiceAssignmentId = assignment.Id,
                    StorageFileId = fileId,
                    CreatedAt = now,
                });
            }
        }

        ret.Status = EquipmentReturnStatus.Processing;
        ret.AssignedStorekeeperId = null;
        ret.ReturnedAt = now;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            ret.Note = string.IsNullOrWhiteSpace(ret.Note)
                ? $"[KTV thu hồi]: {request.Note.Trim()}"
                : $"{ret.Note}\n[KTV thu hồi]: {request.Note.Trim()}";
        }
        ret.UpdatedAt = now;

        var hasRemainingRetrieval = await db.EquipmentReturns.AsNoTracking()
            .AnyAsync(r => r.ServiceAssignmentId == assignment.Id
                           && r.Id != ret.Id
                           && r.Status == EquipmentReturnStatus.Draft, ct);

        if (!hasRemainingRetrieval && assignment.Status != ServiceAssignmentStatus.Completed)
        {
            assignment.Status = ServiceAssignmentStatus.Completed;
            assignment.CompletedAt = now;
            assignment.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var contract = await db.RentalContracts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == ret.RentalContractId, ct);

        var notifyIds = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where r.Code == "LEAD_STOREKEEPER" || r.Code == "STOREKEEPER"
            select ur.UserId
        ).Distinct().ToListAsync(ct);

        if (contract?.ApprovedBy.HasValue == true)
            notifyIds.Add(contract.ApprovedBy.Value);

        if (notifyIds.Count > 0)
        {
            await notificationDispatcher.NotifyUsersAsync(
                notifyIds.Distinct(),
                $"KTV đã thu hồi máy về kho ({ret.ReturnCode})",
                $"Đội kỹ thuật đã hoàn tất thu hồi tại hiện trường cho hợp đồng {contract?.ContractCode ?? ""} ({returnedCount} máy thu về, {missingCount} máy thiếu/mất). Thủ kho vui lòng vào Nhận trả kiểm nhận nhập kho phiếu {ret.ReturnCode}.",
                "RETRIEVAL_READY_FOR_WAREHOUSE",
                cancellationToken: ct);
        }

        return new TechnicianCompleteRetrievalResponse(
            ret.Id,
            ret.ReturnCode,
            returnedCount,
            missingCount,
            $"Đã xác nhận thu hồi {returnedCount} máy ({missingCount} thiếu/mất) — Chuyển phiếu {ret.ReturnCode} về Kho kiểm nhận.");
    }

    // ================================================================ HỖ TRỢ NỘI BỘ

    private TechnicianMediaAttachmentDto BuildMediaDto(StorageFile f)
    {
        var isVideo = f.ResourceType == CloudinaryResourceType.Video;
        var format = string.IsNullOrWhiteSpace(f.ContentType)
            ? (isVideo ? "mp4" : "jpg")
            : f.ContentType;
        var url = uploadService.GetPrivateImageUrl(
            $"{f.BucketName}/{f.ObjectKey}",
            f.ResourceType.ToCloudinaryNetResourceType(),
            format);
        return new TechnicianMediaAttachmentDto(
            f.Id,
            url,
            isVideo ? "VIDEO" : "IMAGE",
            f.OriginalName,
            f.SizeBytes);
    }

    private static string NormalizeIssueType(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "DAMAGED" => "DAMAGED",
        "MALFUNCTION" => "MALFUNCTION",
        "OTHER" => "OTHER",
        _ => "DAMAGED",
    };

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

    private async Task EnsureLeaderOfAssignmentAsync(Guid assignmentId, Guid staffId, CancellationToken ct)
    {
        var member = await db.ServiceAssignmentMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ServiceAssignmentId == assignmentId && m.StaffId == staffId, ct);

        if (member is null)
            throw new AppException("FORBIDDEN_ASSIGNMENT", "Bạn không thuộc chuyến công tác này.", StatusCodes.Status403Forbidden);

        if (!string.Equals(member.Role, "LEADER", StringComparison.OrdinalIgnoreCase))
            throw new AppException("LEADER_ONLY_ACTION", "Chỉ Kỹ thuật viên trưởng của chuyến mới được phép thực hiện thao tác này.", StatusCodes.Status403Forbidden);
    }

    private async Task EnsureValidUploadedMediaAsync(
        HashSet<Guid> fileIds,
        Guid currentUserId,
        bool allowVideo,
        CancellationToken ct)
    {
        if (fileIds.Count == 0) return;

        var files = await db.StorageFiles.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.UploadedBy, f.ContentType, f.ResourceType })
            .ToListAsync(ct);

        if (files.Count != fileIds.Count || files.Any(f => f.UploadedBy != currentUserId))
            throw new AppException("ATTACHMENT_INVALID", "Tệp minh chứng không hợp lệ hoặc không phải do bạn tải lên.", StatusCodes.Status400BadRequest);

        foreach (var f in files)
        {
            var fmt = (f.ContentType ?? "").Trim().ToLowerInvariant();
            if (f.ResourceType == CloudinaryResourceType.Video)
            {
                if (!allowVideo)
                {
                    throw new AppException(
                        "ITEM_VIDEO_NOT_ALLOWED",
                        "Ảnh minh chứng riêng cho từng máy lỗi chỉ hỗ trợ hình ảnh (jpg, png, webp), không hỗ trợ video.",
                        StatusCodes.Status400BadRequest);
                }

                if (!AllowedVideoFormats.Contains(fmt))
                {
                    throw new AppException(
                        "ATTACHMENT_FORMAT_INVALID",
                        "Video minh chứng phải có định dạng mp4, mov, m4v, webm, mkv, avi hoặc 3gp.",
                        StatusCodes.Status400BadRequest);
                }
            }
            else if (!AllowedImageFormats.Contains(fmt))
            {
                throw new AppException(
                    "ATTACHMENT_FORMAT_INVALID",
                    "Ảnh minh chứng phải có định dạng jpg, jpeg, png hoặc webp.",
                    StatusCodes.Status400BadRequest);
            }
        }
    }

    private async Task CloseAssignmentIfNoRemainingStopsAsync(
        ServiceAssignment assignment,
        Guid excludeHandoverId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var hasRemaining = await db.HandoverNotes.AsNoTracking()
            .AnyAsync(n => n.ServiceAssignmentId == assignment.Id
                           && n.Id != excludeHandoverId
                           && n.Status != HandoverNoteStatus.Completed
                           && n.Status != HandoverNoteStatus.Cancelled, ct);

        if (!hasRemaining && assignment.Status != ServiceAssignmentStatus.Completed)
        {
            assignment.Status = ServiceAssignmentStatus.Completed;
            assignment.CompletedAt = now;
            assignment.UpdatedAt = now;
        }
    }

    private void ChangeContractStatus(RentalContract contract, RentalContractStatus newStatus, Guid changedBy, string note)
    {
        var previous = contract.Status;
        var now = DateTimeOffset.UtcNow;
        contract.Status = newStatus;
        contract.UpdatedAt = now;
        db.ContractStatusHistory.Add(new ContractStatusHistory
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contract.Id,
            PreviousStatus = previous,
            Status = newStatus,
            ChangedBy = changedBy,
            ChangedAt = now,
            Note = note,
        });
    }
}
