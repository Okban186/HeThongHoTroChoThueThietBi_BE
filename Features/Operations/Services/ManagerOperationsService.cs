using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Services;

public sealed class ManagerOperationsService(
    AppDbContext db,
    RouteOptimizationService routeService,
    IFileUploadService fileUploadService,
    INotificationDispatcher notificationDispatcher)
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeOnly DispatchOpenTime = new(8, 0);
    private static readonly TimeOnly DispatchCloseTime = new(18, 0);

    // =========================================================================
    // 1. KIỂM TRA TRÙNG LỊCH KỸ THUẬT VIÊN
    // =========================================================================
    public async Task<List<TechnicianAvailabilityResponse>> GetTechniciansAvailabilityAsync(
        DateTimeOffset? startAt,
        int? durationMinutes,
        Guid? excludeAssignmentId = null,
        CancellationToken ct = default)
    {
        var techRoleId = await db.Roles.AsNoTracking()
            .Where(r => r.Code == "TECHNICIAN")
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct);

        var technicians = await (
            from s in db.Staff.AsNoTracking()
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            join ur in db.UserRoles.AsNoTracking() on u.Id equals ur.UserId
            where ur.RoleId == techRoleId && s.Status == StaffStatus.Active
            orderby u.FullName
            select new { StaffId = s.Id, UserId = u.Id, s.EmployeeCode, u.FullName, u.Phone }
        ).ToListAsync(ct);

        if (!startAt.HasValue || !durationMinutes.HasValue || durationMinutes <= 0)
        {
            return technicians.Select(t => new TechnicianAvailabilityResponse(
                t.StaffId, t.UserId, t.EmployeeCode, t.FullName, t.Phone, true, null, null, null)).ToList();
        }

        var reqStart = startAt.Value.ToUniversalTime();
        var reqEnd = reqStart.AddMinutes(durationMinutes.Value);
        var windowStart = reqStart.AddHours(-12);
        var windowEnd = reqEnd.AddHours(12);

        var activeQuery =
            from m in db.ServiceAssignmentMembers.AsNoTracking()
            join a in db.ServiceAssignments.AsNoTracking() on m.ServiceAssignmentId equals a.Id
            where (a.Status == ServiceAssignmentStatus.Pending || a.Status == ServiceAssignmentStatus.InProgress)
                  && a.ScheduledStartAt != null
                  && a.ScheduledStartAt >= windowStart
                  && a.ScheduledStartAt <= windowEnd
            select new { a.Id, m.StaffId, a.Title, Start = a.ScheduledStartAt!.Value, Duration = a.EstimatedDurationMinutes ?? 60 };

        if (excludeAssignmentId.HasValue)
            activeQuery = activeQuery.Where(x => x.Id != excludeAssignmentId.Value);

        var activeAssignments = await activeQuery.ToListAsync(ct);

        return technicians.Select(t =>
        {
            var conflict = activeAssignments.FirstOrDefault(a =>
                a.StaffId == t.StaffId &&
                a.Start < reqEnd &&
                a.Start.AddMinutes(a.Duration) > reqStart);

            return new TechnicianAvailabilityResponse(
                t.StaffId, t.UserId, t.EmployeeCode, t.FullName, t.Phone,
                conflict is null,
                conflict?.Title,
                conflict?.Start,
                conflict?.Start.AddMinutes(conflict.Duration));
        }).ToList();
    }

    // =========================================================================
    // 2. XEM TRƯỚC LỘ TRÌNH, TẠO CHUYẾN MỚI & GHÉP CHUYẾN PENDING CÓ SẴN
    // =========================================================================
    /// <summary>
    /// Trả về các hợp đồng cần Manager xếp chuyến giao:
    /// 1) Hợp đồng PREPARING chưa có phiếu đang gắn vào chuyến nào (gồm HĐ mới, phiếu Kho đã soạn chưa gán chuyến, hoặc phiếu giao thất bại chờ xếp lịch lại).
    /// 2) Hợp đồng PARTIALLY_DELIVERED đang có phiếu giao bù SUPPLEMENTARY chưa gắn chuyến.
    /// </summary>
    public async Task<List<UnscheduledDeliveryContractResponse>> GetUnscheduledDeliveryContractsAsync(
        Guid managerId, CancellationToken ct = default)
    {
        var currentlyScheduledContractIds = db.HandoverNotes.AsNoTracking()
            .Where(h => h.ServiceAssignmentId != null
                        && h.Status != HandoverNoteStatus.Completed
                        && h.Status != HandoverNoteStatus.Cancelled)
            .Select(h => h.RentalContractId);

        // Quy tắc bắt buộc: phải hoàn tất phiếu thu hồi máy lỗi (DEFECTIVE_ON_DELIVERY) trước khi xếp lịch giao bù
        var contractsWaitingDefectiveReturn = db.EquipmentReturns.AsNoTracking()
            .Where(r => r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                        && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
            .Select(r => r.RentalContractId);

        // Hợp đồng đang DELIVERED nhưng có sự cố RETURN_REQUIRED đã thu hồi xong máy & đã xong khâu duyệt/thanh toán phạt -> sẵn sàng giao máy thay thế (INCIDENT_REPLACEMENT)
        var readyIncidentByContract = await GetReadyIncidentReplacementsForManagerAsync(managerId, ct);
        var readyIncidentContractIds = readyIncidentByContract.Keys.ToList();

        var rows = await (
            from c in db.RentalContracts.AsNoTracking()
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where c.ApprovedBy == managerId
                  && !currentlyScheduledContractIds.Contains(c.Id)
                  && (c.Status == RentalContractStatus.Preparing
                      || (c.Status == RentalContractStatus.PartiallyDelivered && !contractsWaitingDefectiveReturn.Contains(c.Id))
                      || (c.Status == RentalContractStatus.Delivered && readyIncidentContractIds.Contains(c.Id)))
            orderby c.Status == RentalContractStatus.Delivered descending,
                    c.Status == RentalContractStatus.PartiallyDelivered descending,
                    c.StartDate,
                    c.ContractCode
            select new
            {
                c.Id,
                c.ContractCode,
                c.Status,
                CompanyName = co.Name,
                c.StartDate,
                c.EndDate,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address
            }
        ).ToListAsync(ct);

        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToList();
        var qtyByContract = await db.RentalContractItems.AsNoTracking()
            .Where(i => ids.Contains(i.RentalContractId))
            .GroupBy(i => i.RentalContractId)
            .Select(g => new { g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty, ct);

        var openNotes = await db.HandoverNotes.AsNoTracking()
            .Where(h => ids.Contains(h.RentalContractId)
                        && h.ServiceAssignmentId == null
                        && h.Status != HandoverNoteStatus.Completed
                        && h.Status != HandoverNoteStatus.Cancelled)
            .OrderByDescending(h => h.UpdatedAt)
            .ToListAsync(ct);

        var openNoteByContract = openNotes
            .GroupBy(h => h.RentalContractId)
            .ToDictionary(g => g.Key, g => g.First());

        return rows.Select(r =>
        {
            openNoteByContract.TryGetValue(r.Id, out var note);
            var failedAttempts = note?.FailedAt != null
                ? Math.Max(1, note.DeliveryAttemptNumber)
                : 0;
            return new UnscheduledDeliveryContractResponse(
                r.Id,
                r.ContractCode,
                r.CompanyName,
                r.StartDate,
                r.EndDate,
                r.Address,
                qtyByContract.GetValueOrDefault(r.Id, 0),
                note?.Id,
                note?.HandoverCode,
                note?.HandoverType ?? (r.Status == RentalContractStatus.Delivered
                    ? "INCIDENT_REPLACEMENT"
                    : r.Status == RentalContractStatus.PartiallyDelivered ? "SUPPLEMENTARY" : "INITIAL"),
                failedAttempts,
                note?.FailedReason,
                note?.FailedAt);
        }).ToList();
    }


    /// <summary>Tổng quan các hợp đồng giao thiếu (PARTIALLY_DELIVERED): đang chờ thu hồi máy lỗi hay đã sẵn sàng xếp lịch giao bù.</summary>
    public async Task<List<SupplementaryDeliveryOverviewResponse>> GetSupplementaryDeliveryOverviewAsync(
        Guid managerId, CancellationToken ct = default)
    {
        var rows = await (
            from c in db.RentalContracts.AsNoTracking()
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where c.ApprovedBy == managerId && c.Status == RentalContractStatus.PartiallyDelivered
            orderby c.UpdatedAt descending
            select new
            {
                c.Id,
                c.ContractCode,
                CompanyName = co.Name,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address,
                c.StartDate,
                c.EndDate
            }
        ).ToListAsync(ct);

        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToList();

        var totalByContract = await db.RentalContractItems.AsNoTracking()
            .Where(i => ids.Contains(i.RentalContractId))
            .GroupBy(i => i.RentalContractId)
            .Select(g => new { g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Qty, ct);

        var defectivePairs = await (
            from iss in db.HandoverIssues.AsNoTracking()
            join issItem in db.HandoverIssueItems.AsNoTracking() on iss.Id equals issItem.HandoverIssueId
            join hn in db.HandoverNotes.AsNoTracking() on iss.HandoverNoteId equals hn.Id
            where ids.Contains(hn.RentalContractId)
                  && iss.Status != HandoverIssueStatus.Cancelled
                  && issItem.Status != HandoverIssueItemStatus.Cancelled
            select new { hn.Id, issItem.EquipmentId }
        ).ToListAsync(ct);
        var defectiveSet = defectivePairs.Select(x => (x.Id, x.EquipmentId)).ToHashSet();

        var completedItems = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            where ids.Contains(hn.RentalContractId) && hn.Status == HandoverNoteStatus.Completed
            select new { hn.RentalContractId, NoteId = hn.Id, hi.EquipmentId }
        ).ToListAsync(ct);

        var deliveredGoodByContract = completedItems
            .Where(x => !defectiveSet.Contains((x.NoteId, x.EquipmentId)))
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => g.Count());

        var pendingDefReturns = await db.EquipmentReturns.AsNoTracking()
            .Where(r => ids.Contains(r.RentalContractId)
                        && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                        && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        var pendingDefByContract = pendingDefReturns
            .GroupBy(r => r.RentalContractId)
            .ToDictionary(g => g.Key, g => g.First());

        var openSuppNotes = await db.HandoverNotes.AsNoTracking()
            .Where(h => ids.Contains(h.RentalContractId)
                        && h.HandoverType == "SUPPLEMENTARY"
                        && h.Status != HandoverNoteStatus.Completed
                        && h.Status != HandoverNoteStatus.Cancelled)
            .OrderByDescending(h => h.UpdatedAt)
            .ToListAsync(ct);
        var openSuppByContract = openSuppNotes
            .GroupBy(h => h.RentalContractId)
            .ToDictionary(g => g.Key, g => g.First());

        return rows.Select(r =>
        {
            var total = totalByContract.GetValueOrDefault(r.Id, 0);
            var deliveredGood = deliveredGoodByContract.GetValueOrDefault(r.Id, 0);
            var missing = Math.Max(0, total - deliveredGood);
            pendingDefByContract.TryGetValue(r.Id, out var pendingRet);
            openSuppByContract.TryGetValue(r.Id, out var suppNote);

            var waitingReturn = pendingRet is not null;
            var alreadyScheduled = suppNote?.ServiceAssignmentId != null;
            var canSchedule = !waitingReturn && !alreadyScheduled && missing > 0;

            return new SupplementaryDeliveryOverviewResponse(
                r.Id,
                r.ContractCode,
                r.CompanyName,
                r.Address,
                r.StartDate,
                r.EndDate,
                total,
                deliveredGood,
                missing,
                waitingReturn,
                pendingRet?.ReturnCode,
                suppNote?.Id,
                suppNote?.HandoverCode,
                suppNote is null ? null : EnumName.ToDatabaseValue(suppNote.Status),
                alreadyScheduled,
                canSchedule);
        }).ToList();
    }

    public async Task<List<MergeableTripResponse>> GetMergeableTripsAsync(
        DateOnly date, Guid managerId, CancellationToken ct = default)
    {
        var startOfDay = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime();
        var endOfDay = startOfDay.AddDays(1);

        var trips = await db.ServiceAssignments.AsNoTracking()
            .Where(a => a.Type == "HANDOVER"
                        && a.Status == ServiceAssignmentStatus.Pending
                        && a.CreatedBy == managerId
                        && a.ScheduledStartAt >= startOfDay
                        && a.ScheduledStartAt < endOfDay)
            .OrderBy(a => a.ScheduledStartAt)
            .ToListAsync(ct);

        if (trips.Count == 0) return [];

        var ids = trips.Select(t => t.Id).ToList();

        var members = await (
            from m in db.ServiceAssignmentMembers.AsNoTracking()
            join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where ids.Contains(m.ServiceAssignmentId)
            select new { m.ServiceAssignmentId, m.Role, u.FullName }
        ).ToListAsync(ct);

        var notes = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            where h.ServiceAssignmentId != null
                  && ids.Contains(h.ServiceAssignmentId.Value)
                  && h.Status != HandoverNoteStatus.Cancelled
            select new { AssignmentId = h.ServiceAssignmentId!.Value, c.ContractCode }
        ).ToListAsync(ct);

        return trips.Select(t =>
        {
            var mList = members.Where(m => m.ServiceAssignmentId == t.Id).ToList();
            var leader = mList.FirstOrDefault(m => m.Role == "LEADER")?.FullName ?? "Chưa rõ";
            var others = mList.Where(m => m.Role != "LEADER").Select(m => m.FullName).ToList();
            var codes = notes.Where(n => n.AssignmentId == t.Id).Select(n => n.ContractCode).ToList();
            var start = t.ScheduledStartAt!.Value;
            var duration = t.EstimatedDurationMinutes ?? 60;

            return new MergeableTripResponse(
                t.Id, t.Title, start, start.AddMinutes(duration), duration,
                leader, others, codes.Count, codes);
        }).ToList();
    }

    public async Task<OptimizedTripResponse> PreviewDeliveryTripAsync(
        PreviewDeliveryTripRequest req, Guid managerId, CancellationToken ct = default)
    {
        var effectiveStart = req.ScheduledStartAt;
        var allContractIds = req.ContractIds.Distinct().ToList();

        if (req.ExistingAssignmentId.HasValue)
        {
            var existing = await db.ServiceAssignments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == req.ExistingAssignmentId.Value, ct)
                ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến đi cần ghép.", StatusCodes.Status404NotFound);

            if (existing.Status != ServiceAssignmentStatus.Pending || existing.Type != "HANDOVER")
                throw new AppException("ASSIGNMENT_NOT_MERGEABLE", "Chỉ có thể ghép vào chuyến giao đang ở trạng thái PENDING (chưa xuất phát).", StatusCodes.Status409Conflict);

            effectiveStart = existing.ScheduledStartAt ?? req.ScheduledStartAt;

            var existingContractIds = await db.HandoverNotes.AsNoTracking()
                .Where(h => h.ServiceAssignmentId == existing.Id && h.Status != HandoverNoteStatus.Cancelled)
                .Select(h => h.RentalContractId)
                .ToListAsync(ct);

            allContractIds = existingContractIds.Concat(allContractIds).Distinct().ToList();
        }
        else
        {
            EnsureWithinDispatchHours(effectiveStart);
        }

        var (depot, waypoints, _) = await PrepareDeliveryWaypointsAsync(
            allContractIds, effectiveStart, managerId, existingAssignmentId: req.ExistingAssignmentId, ct);

        return await routeService.OptimizeRoundTripAsync(depot, waypoints, ct);
    }

    public async Task<DailyScheduleItemResponse> CreateDeliveryTripAsync(
        CreateDeliveryTripRequest req, Guid managerId, CancellationToken ct = default)
    {
        EnsureWithinDispatchHours(req.ScheduledStartAt);

        var (depot, waypoints, contracts) = await PrepareDeliveryWaypointsAsync(
            req.ContractIds, req.ScheduledStartAt, managerId, existingAssignmentId: null, ct);

        var route = await routeService.OptimizeRoundTripAsync(depot, waypoints, ct);

        var allStaffIds = req.MemberStaffIds.Append(req.LeaderStaffId).Distinct().ToList();
        await EnsureNoTechnicianConflictAsync(allStaffIds, req.ScheduledStartAt, route.TotalDurationMinutes, excludeAssignmentId: null, ct);

        var now = DateTimeOffset.UtcNow;
        var assignment = new ServiceAssignment
        {
            Id = Guid.CreateVersion7(),
            Type = "HANDOVER",
            Title = string.IsNullOrWhiteSpace(req.Title)
                ? $"Giao lắp {contracts.Count} điểm ({string.Join(", ", contracts.Select(c => c.ContractCode))})"
                : req.Title.Trim(),
            Description = req.Note?.Trim(),
            Status = ServiceAssignmentStatus.Pending,
            AssignedAt = now,
            ScheduledStartAt = req.ScheduledStartAt.ToUniversalTime(),
            EstimatedDurationMinutes = route.TotalDurationMinutes,
            CreatedBy = managerId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ServiceAssignments.Add(assignment);

        foreach (var staffId in allStaffIds)
        {
            db.ServiceAssignmentMembers.Add(new ServiceAssignmentMember
            {
                Id = Guid.CreateVersion7(),
                ServiceAssignmentId = assignment.Id,
                StaffId = staffId,
                Role = staffId == req.LeaderStaffId ? "LEADER" : "MEMBER"
            });
        }

        var handoverDate = DateOnly.FromDateTime(req.ScheduledStartAt.ToOffset(VietnamOffset).Date);
        var reps = await db.RentalContractRepresentatives.AsNoTracking()
            .Where(r => req.ContractIds.Contains(r.RentalContractId))
            .ToDictionaryAsync(r => r.RentalContractId, ct);

        var existingOpenNotes = await db.HandoverNotes
            .Where(h => req.ContractIds.Contains(h.RentalContractId)
                        && h.ServiceAssignmentId == null
                        && h.Status != HandoverNoteStatus.Completed
                        && h.Status != HandoverNoteStatus.Cancelled)
            .ToListAsync(ct);

        var openNoteIds = existingOpenNotes.Select(n => n.Id).ToList();
        var itemCountByNote = openNoteIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.HandoverItems.AsNoTracking()
                .Where(i => openNoteIds.Contains(i.HandoverNoteId))
                .GroupBy(i => i.HandoverNoteId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        foreach (var stop in route.Stops)
        {
            var contract = contracts.First(c => c.Id == stop.ReferenceId);
            reps.TryGetValue(contract.Id, out var rep);

            var reusableNote = existingOpenNotes.FirstOrDefault(n => n.RentalContractId == contract.Id);
            if (reusableNote is not null)
            {
                reusableNote.ServiceAssignmentId = assignment.Id;
                reusableNote.HandoverDate = handoverDate;
                reusableNote.StopOrder = stop.StopOrder;
                reusableNote.DeliveryLatitude = stop.Latitude;
                reusableNote.DeliveryLongitude = stop.Longitude;
                reusableNote.EstimatedTravelMinutes = stop.TravelMinutesFromPrevious;
                reusableNote.EstimatedInstallMinutes = stop.InstallMinutes;
                if (reusableNote.FailedAt.HasValue
                    && reusableNote.Status == HandoverNoteStatus.Preparing
                    && itemCountByNote.GetValueOrDefault(reusableNote.Id) > 0)
                {
                    reusableNote.Status = HandoverNoteStatus.Shipping;
                }
                reusableNote.UpdatedAt = now;
            }
            else
            {
                var isIncRepl = contract.Status == RentalContractStatus.Delivered;
                var isSupp = contract.Status == RentalContractStatus.PartiallyDelivered;
                var readyIncMap = isIncRepl ? await GetReadyIncidentReplacementsForManagerAsync(managerId, ct) : [];
                Guid? incidentId = isIncRepl && readyIncMap.TryGetValue(contract.Id, out var readyInc) ? readyInc.IncidentId : null;
                Guid? parentHandoverId = (isSupp || isIncRepl)
                    ? await db.HandoverNotes.AsNoTracking()
                        .Where(h => h.RentalContractId == contract.Id && h.Status == HandoverNoteStatus.Completed)
                        .OrderByDescending(h => h.UpdatedAt)
                        .Select(h => (Guid?)h.Id)
                        .FirstOrDefaultAsync(ct)
                    : null;

                db.HandoverNotes.Add(new HandoverNote
                {
                    Id = Guid.CreateVersion7(),
                    HandoverCode = GenerateHandoverCode(),
                    RentalContractId = contract.Id,
                    ServiceAssignmentId = assignment.Id,
                    HandoverDate = handoverDate,
                    HandoverType = isIncRepl ? "INCIDENT_REPLACEMENT" : isSupp ? "SUPPLEMENTARY" : "INITIAL",
                    ParentHandoverId = parentHandoverId,
                    IncidentId = incidentId,
                    DeliveryAttemptNumber = 1,
                    ReceivedByName = rep?.FullName ?? "Người nhận tại công ty",
                    ReceivedByPhone = rep?.Phone,
                    StopOrder = stop.StopOrder,
                    DeliveryLatitude = stop.Latitude,
                    DeliveryLongitude = stop.Longitude,
                    EstimatedTravelMinutes = stop.TravelMinutesFromPrevious,
                    EstimatedInstallMinutes = stop.InstallMinutes,
                    Note = $"Điểm #{stop.StopOrder} | Di chuyển ~{stop.TravelMinutesFromPrevious}p ({stop.DistanceKmFromPrevious}km), Lắp đặt ~{stop.InstallMinutes}p",
                    Status = HandoverNoteStatus.Preparing,
                    CreatedBy = managerId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        var vnStart = req.ScheduledStartAt.ToOffset(VietnamOffset);
        await NotifyAssignedTeamAsync(
            allStaffIds,
            req.LeaderStaffId,
            assignment.Title,
            $"Khởi hành {vnStart:HH:mm dd/MM/yyyy} • {route.Stops.Count} điểm giao • Dự kiến {route.TotalDurationMinutes} phút.",
            "DELIVERY_ASSIGNMENT",
            ct);

        await db.SaveChangesAsync(ct);
        return (await GetDailyScheduleAsync(handoverDate, null, ct)).First(x => x.AssignmentId == assignment.Id);
    }

    /// <summary>Ghép thêm 1 hoặc nhiều hợp đồng vào chuyến HANDOVER đang PENDING có sẵn (giữ nguyên đội cũ, tự tối ưu lại đường và check trùng lịch).</summary>
    public async Task<DailyScheduleItemResponse> MergeIntoDeliveryTripAsync(
        Guid assignmentId,
        MergeIntoDeliveryTripRequest req,
        Guid managerId,
        CancellationToken ct = default)
    {
        var assignment = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId, ct)
            ?? throw new AppException("ASSIGNMENT_NOT_FOUND", "Không tìm thấy chuyến đi.", StatusCodes.Status404NotFound);

        if (assignment.Type != "HANDOVER" || assignment.Status != ServiceAssignmentStatus.Pending)
            throw new AppException("ASSIGNMENT_NOT_MERGEABLE", "Chỉ được ghép điểm vào chuyến giao đang ở trạng thái PENDING (chưa xuất phát).", StatusCodes.Status409Conflict);

        if (assignment.CreatedBy != managerId)
            throw new AppException("FORBIDDEN_MANAGER", "Bạn chỉ có thể ghép điểm vào chuyến do mình lập.", StatusCodes.Status403Forbidden);

        var scheduledStart = assignment.ScheduledStartAt
            ?? throw new AppException("INVALID_SCHEDULE", "Chuyến đi chưa có giờ khởi hành.", StatusCodes.Status400BadRequest);

        var existingNotes = await db.HandoverNotes
            .Where(h => h.ServiceAssignmentId == assignmentId && h.Status != HandoverNoteStatus.Cancelled)
            .ToListAsync(ct);

        var existingContractIds = existingNotes.Select(h => h.RentalContractId).ToHashSet();
        var newContractIds = req.ContractIds.Distinct().Where(id => !existingContractIds.Contains(id)).ToList();

        if (newContractIds.Count == 0)
            throw new AppException("NO_NEW_CONTRACTS", "Các hợp đồng chọn đã nằm trong chuyến đi này.", StatusCodes.Status400BadRequest);

        var combinedIds = existingContractIds.Concat(newContractIds).ToList();

        // Tối ưu lại toàn bộ tuyến (điểm cũ + điểm mới)
        var (depot, waypoints, contracts) = await PrepareDeliveryWaypointsAsync(
            combinedIds, scheduledStart, managerId, existingAssignmentId: assignmentId, ct);

        var route = await routeService.OptimizeRoundTripAsync(depot, waypoints, ct);

        // Kiểm tra xem khi thời lượng kéo dài thêm, đội kỹ thuật cũ có bị đụng lịch chuyến sau không
        var members = await db.ServiceAssignmentMembers.AsNoTracking()
            .Where(m => m.ServiceAssignmentId == assignmentId)
            .ToListAsync(ct);
        var staffIds = members.Select(m => m.StaffId).ToList();
        var leaderId = members.FirstOrDefault(m => m.Role == "LEADER")?.StaffId ?? staffIds.First();

        await EnsureNoTechnicianConflictAsync(staffIds, scheduledStart, route.TotalDurationMinutes, excludeAssignmentId: assignmentId, ct);

        var now = DateTimeOffset.UtcNow;
        var handoverDate = DateOnly.FromDateTime(scheduledStart.ToOffset(VietnamOffset).Date);
        var reps = await db.RentalContractRepresentatives.AsNoTracking()
            .Where(r => newContractIds.Contains(r.RentalContractId))
            .ToDictionaryAsync(r => r.RentalContractId, ct);

        var unscheduledOpenNotes = await db.HandoverNotes
            .Where(h => newContractIds.Contains(h.RentalContractId)
                        && h.ServiceAssignmentId == null
                        && h.Status != HandoverNoteStatus.Completed
                        && h.Status != HandoverNoteStatus.Cancelled)
            .ToListAsync(ct);

        var openMergeNoteIds = unscheduledOpenNotes.Select(n => n.Id).ToList();
        var mergeItemCountByNote = openMergeNoteIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.HandoverItems.AsNoTracking()
                .Where(i => openMergeNoteIds.Contains(i.HandoverNoteId))
                .GroupBy(i => i.HandoverNoteId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Cập nhật thứ tự mới cho điểm cũ & gắn/thêm phiếu bàn giao cho điểm mới
        foreach (var stop in route.Stops)
        {
            var existingNote = existingNotes.FirstOrDefault(n => n.RentalContractId == stop.ReferenceId);
            if (existingNote is not null)
            {
                existingNote.StopOrder = stop.StopOrder;
                existingNote.DeliveryLatitude = stop.Latitude;
                existingNote.DeliveryLongitude = stop.Longitude;
                existingNote.EstimatedTravelMinutes = stop.TravelMinutesFromPrevious;
                existingNote.EstimatedInstallMinutes = stop.InstallMinutes;
                existingNote.Note = $"Điểm #{stop.StopOrder} | Di chuyển ~{stop.TravelMinutesFromPrevious}p ({stop.DistanceKmFromPrevious}km), Lắp đặt ~{stop.InstallMinutes}p";
                existingNote.UpdatedAt = now;
            }
            else
            {
                var reusableNote = unscheduledOpenNotes.FirstOrDefault(n => n.RentalContractId == stop.ReferenceId);
                if (reusableNote is not null)
                {
                    reusableNote.ServiceAssignmentId = assignment.Id;
                    reusableNote.HandoverDate = handoverDate;
                    reusableNote.StopOrder = stop.StopOrder;
                    reusableNote.DeliveryLatitude = stop.Latitude;
                    reusableNote.DeliveryLongitude = stop.Longitude;
                    reusableNote.EstimatedTravelMinutes = stop.TravelMinutesFromPrevious;
                    reusableNote.EstimatedInstallMinutes = stop.InstallMinutes;
                    if (reusableNote.FailedAt.HasValue
                        && reusableNote.Status == HandoverNoteStatus.Preparing
                        && mergeItemCountByNote.GetValueOrDefault(reusableNote.Id) > 0)
                    {
                        reusableNote.Status = HandoverNoteStatus.Shipping;
                    }
                    reusableNote.UpdatedAt = now;
                }
                else
                {
                    reps.TryGetValue(stop.ReferenceId, out var rep);
                    var contractForStop = contracts.First(c => c.Id == stop.ReferenceId);
                    var isIncRepl = contractForStop.Status == RentalContractStatus.Delivered;
                    var isSupp = contractForStop.Status == RentalContractStatus.PartiallyDelivered;
                    var readyIncMap = isIncRepl ? await GetReadyIncidentReplacementsForManagerAsync(managerId, ct) : [];
                    Guid? incidentId = isIncRepl && readyIncMap.TryGetValue(stop.ReferenceId, out var readyInc) ? readyInc.IncidentId : null;
                    Guid? parentHandoverId = (isSupp || isIncRepl)
                        ? await db.HandoverNotes.AsNoTracking()
                            .Where(h => h.RentalContractId == stop.ReferenceId && h.Status == HandoverNoteStatus.Completed)
                            .OrderByDescending(h => h.UpdatedAt)
                            .Select(h => (Guid?)h.Id)
                            .FirstOrDefaultAsync(ct)
                        : null;

                    db.HandoverNotes.Add(new HandoverNote
                    {
                        Id = Guid.CreateVersion7(),
                        HandoverCode = GenerateHandoverCode(),
                        RentalContractId = stop.ReferenceId,
                        ServiceAssignmentId = assignment.Id,
                        HandoverDate = handoverDate,
                        HandoverType = isIncRepl ? "INCIDENT_REPLACEMENT" : isSupp ? "SUPPLEMENTARY" : "INITIAL",
                        ParentHandoverId = parentHandoverId,
                        IncidentId = incidentId,
                        DeliveryAttemptNumber = 1,
                        ReceivedByName = rep?.FullName ?? "Người nhận tại công ty",
                        ReceivedByPhone = rep?.Phone,
                        StopOrder = stop.StopOrder,
                        DeliveryLatitude = stop.Latitude,
                        DeliveryLongitude = stop.Longitude,
                        EstimatedTravelMinutes = stop.TravelMinutesFromPrevious,
                        EstimatedInstallMinutes = stop.InstallMinutes,
                        Note = $"Điểm #{stop.StopOrder} | Di chuyển ~{stop.TravelMinutesFromPrevious}p ({stop.DistanceKmFromPrevious}km), Lắp đặt ~{stop.InstallMinutes}p",
                        Status = HandoverNoteStatus.Preparing,
                        CreatedBy = managerId,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
            }
        }

        assignment.Title = $"Giao lắp {contracts.Count} điểm ({string.Join(", ", contracts.Select(c => c.ContractCode))})";
        assignment.EstimatedDurationMinutes = route.TotalDurationMinutes;
        assignment.UpdatedAt = now;

        await NotifyAssignedTeamAsync(
            staffIds,
            leaderId,
            $"Cập nhật ghép tuyến: {assignment.Title}",
            $"Chuyến đã ghép thêm {newContractIds.Count} điểm giao. Tổng {route.Stops.Count} điểm • Dự kiến {route.TotalDurationMinutes} phút.",
            "DELIVERY_UPDATED",
            ct);

        await db.SaveChangesAsync(ct);
        return (await GetDailyScheduleAsync(handoverDate, null, ct)).First(x => x.AssignmentId == assignment.Id);
    }

    private async Task<((double Lat, double Lng, string Address) Depot, List<RouteWaypoint> Waypoints, List<RentalContract> Contracts)>
        PrepareDeliveryWaypointsAsync(
            List<Guid> contractIds,
            DateTimeOffset scheduledStartAt,
            Guid managerId,
            Guid? existingAssignmentId,
            CancellationToken ct)
    {
        var distinctIds = contractIds.Distinct().ToList();
        var contracts = await db.RentalContracts
            .Where(c => distinctIds.Contains(c.Id))
            .ToListAsync(ct);

        if (contracts.Count != distinctIds.Count)
            throw new AppException("CONTRACT_NOT_FOUND", "Có hợp đồng không tồn tại.", StatusCodes.Status404NotFound);

        var deliveryDate = DateOnly.FromDateTime(scheduledStartAt.ToOffset(VietnamOffset).Date);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Add(VietnamOffset));
        if (deliveryDate < today)
            throw new AppException("INVALID_DELIVERY_DATE", "Ngày giao không được ở quá khứ.", StatusCodes.Status400BadRequest);

        // Chỉ chặn trùng nếu hợp đồng đang có phiếu chưa hoàn tất đã gắn vào một chuyến khác
        var alreadyScheduledIds = await db.HandoverNotes.AsNoTracking()
            .Where(h => distinctIds.Contains(h.RentalContractId)
                        && h.ServiceAssignmentId != null
                        && h.Status != HandoverNoteStatus.Completed
                        && h.Status != HandoverNoteStatus.Cancelled
                        && (!existingAssignmentId.HasValue || h.ServiceAssignmentId != existingAssignmentId.Value))
            .Select(h => h.RentalContractId)
            .ToListAsync(ct);

        var pendingDefReturnByContract = (await db.EquipmentReturns.AsNoTracking()
            .Where(r => distinctIds.Contains(r.RentalContractId)
                        && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                        && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
            .Select(r => new { r.RentalContractId, r.ReturnCode })
            .ToListAsync(ct))
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => g.First().ReturnCode);

        foreach (var c in contracts)
        {
            if (c.ApprovedBy != managerId)
                throw new AppException("FORBIDDEN_MANAGER", $"Hợp đồng {c.ContractCode} không thuộc quyền phụ trách của bạn.", StatusCodes.Status403Forbidden);

            if (c.Status == RentalContractStatus.PartiallyDelivered && pendingDefReturnByContract.TryGetValue(c.Id, out var pendingRetCode))
            {
                throw new AppException(
                    "DEFECTIVE_RETURN_NOT_COMPLETED",
                    $"Hợp đồng {c.ContractCode} đang chờ Kho hoàn tất kiểm nhận phiếu thu hồi máy lỗi {pendingRetCode}. Phải thu hồi xong máy lỗi mới được tạo lịch giao bù.",
                    StatusCodes.Status409Conflict);
            }

            if (c.Status == RentalContractStatus.Delivered)
            {
                var readyIncMap = await GetReadyIncidentReplacementsForManagerAsync(managerId, ct);
                if (!readyIncMap.ContainsKey(c.Id))
                {
                    throw new AppException(
                        "INCIDENT_REPLACEMENT_NOT_READY",
                        $"Hợp đồng {c.ContractCode} chưa đủ điều kiện tạo lịch giao máy thay thế sự cố (cần hoàn tất thu hồi máy hỏng, KTV phân định trách nhiệm, và thanh toán/cấn trừ xong khoản bồi thường nếu có).",
                        StatusCodes.Status409Conflict);
                }
            }
            else if (c.Status != RentalContractStatus.Preparing && c.Status != RentalContractStatus.PartiallyDelivered)
            {
                throw new AppException("INVALID_STATUS", $"Hợp đồng {c.ContractCode} phải ở trạng thái Đang chuẩn bị (PREPARING), Giao một phần (PARTIALLY_DELIVERED) hoặc có sự cố sẵn sàng giao máy thay thế.", StatusCodes.Status400BadRequest);
            }

            if (alreadyScheduledIds.Contains(c.Id))
                throw new AppException("ALREADY_SCHEDULED", $"Hợp đồng {c.ContractCode} đã được lên lịch bàn giao trong một chuyến khác rồi.", StatusCodes.Status409Conflict);

            // Ngày giao PHẢI TRƯỚC ngày bắt đầu thuê ít nhất 1 ngày
            // if (deliveryDate >= c.StartDate)
            // {
            //     throw new AppException(
            //         "DELIVERY_AFTER_START_DATE",
            //         $"Ngày giao ({deliveryDate:dd/MM/yyyy}) cho hợp đồng {c.ContractCode} phải trước ngày bắt đầu thuê ({c.StartDate:dd/MM/yyyy}) ít nhất 1 ngày.",
            //         StatusCodes.Status400BadRequest);
            // }
        }

        // Cache tọa độ nếu điểm cũ trong chuyến ghép đã từng geocode
        var cachedCoords = existingAssignmentId.HasValue
            ? await db.HandoverNotes.AsNoTracking()
                .Where(h => h.ServiceAssignmentId == existingAssignmentId.Value && h.DeliveryLatitude != null && h.DeliveryLongitude != null)
                .ToDictionaryAsync(h => h.RentalContractId, h => (h.DeliveryLatitude!.Value, h.DeliveryLongitude!.Value), ct)
            : new Dictionary<Guid, (double, double)>();

        var installMinsByContract = await (
            from item in db.RentalContractItems.AsNoTracking()
            join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
            where distinctIds.Contains(item.RentalContractId)
            group new { item.Quantity, model.EstimatedInstallationMinutes } by item.RentalContractId into g
            select new { g.Key, Minutes = g.Sum(x => x.Quantity * Math.Max(15, x.EstimatedInstallationMinutes)) }
        ).ToDictionaryAsync(x => x.Key, x => x.Minutes, ct);

        var companies = await db.Companies.AsNoTracking()
            .Where(co => contracts.Select(c => c.CompanyId).Contains(co.Id))
            .ToDictionaryAsync(co => co.Id, ct);

        var warehouse = await db.Warehouses
            .Where(w => w.Status == WarehouseStatus.Active)
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("WAREHOUSE_NOT_FOUND", "Hệ thống chưa có kho đang hoạt động.", StatusCodes.Status404NotFound);

        double depotLat, depotLng;
        if (warehouse.Latitude.HasValue && warehouse.Longitude.HasValue)
        {
            depotLat = warehouse.Latitude.Value;
            depotLng = warehouse.Longitude.Value;
        }
        else
        {
            (depotLat, depotLng) = await routeService.ResolveAddressAsync(
                warehouse.Address ?? "TP. Hồ Chí Minh", $"Kho {warehouse.Name}", ct);
            warehouse.Latitude = depotLat;
            warehouse.Longitude = depotLng;
            await db.SaveChangesAsync(ct);
        }

        var depot = (depotLat, depotLng, warehouse.Address ?? warehouse.Name);

        var waypoints = contracts.Select(c =>
        {
            var company = companies[c.CompanyId];
            var addr = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : (company.Address ?? "");
            var install = installMinsByContract.GetValueOrDefault(c.Id, 30);
            var hasCache = cachedCoords.TryGetValue(c.Id, out var coord);
            return new RouteWaypoint(
                c.Id,
                $"{c.ContractCode} - {company.Name}",
                addr,
                hasCache ? coord.Item1 : null,
                hasCache ? coord.Item2 : null,
                install);
        }).ToList();

        return (depot, waypoints, contracts);
    }


    public async Task<List<DailyScheduleItemResponse>> GetDailyScheduleAsync(
        DateOnly date, Guid? staffId, CancellationToken ct = default)
    {
        var startOfDay = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), VietnamOffset).ToUniversalTime();
        var endOfDay = startOfDay.AddDays(1);

        var query = db.ServiceAssignments.AsNoTracking()
            .Where(a => a.ScheduledStartAt >= startOfDay
                        && a.ScheduledStartAt < endOfDay
                        && a.Status != ServiceAssignmentStatus.Cancelled);

        if (staffId.HasValue)
        {
            query = query.Where(a => db.ServiceAssignmentMembers
                .Any(m => m.ServiceAssignmentId == a.Id && m.StaffId == staffId.Value));
        }

        var assignments = await query.OrderBy(a => a.ScheduledStartAt).ToListAsync(ct);
        var ids = assignments.Select(a => a.Id).ToList();

        var members = await (
            from m in db.ServiceAssignmentMembers.AsNoTracking()
            join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where ids.Contains(m.ServiceAssignmentId)
            select new { m.ServiceAssignmentId, m.Role, u.FullName }
        ).ToListAsync(ct);

        var stops = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where h.ServiceAssignmentId != null
                  && ids.Contains(h.ServiceAssignmentId.Value)
                  && h.Status != HandoverNoteStatus.Cancelled
            orderby h.StopOrder
            select new
            {
                AssignmentId = h.ServiceAssignmentId!.Value,
                Dto = new DailyScheduleStopDto(
                    h.Id, h.HandoverCode, c.ContractCode, co.Name,
                    h.StopOrder, h.EstimatedTravelMinutes, h.EstimatedInstallMinutes,
                    EnumName.ToDatabaseValue(h.Status))
            }
        ).ToListAsync(ct);

        return assignments.Select(a =>
        {
            var mList = members.Where(m => m.ServiceAssignmentId == a.Id).ToList();
            var leader = mList.FirstOrDefault(m => m.Role == "LEADER")?.FullName ?? "Chưa rõ";
            var others = mList.Where(m => m.Role != "LEADER").Select(m => m.FullName).ToList();
            var aStops = stops.Where(s => s.AssignmentId == a.Id).Select(s => s.Dto).ToList();
            var duration = a.EstimatedDurationMinutes ?? 60;
            var start = a.ScheduledStartAt ?? a.CreatedAt;

            return new DailyScheduleItemResponse(
                a.Id, a.Type, a.Title, a.Description,
                EnumName.ToDatabaseValue(a.Status),
                start, start.AddMinutes(duration), duration,
                leader, others, Math.Max(1, aStops.Count), aStops);
        }).ToList();
    }

    // =========================================================================
    // 4. TIẾP NHẬN SỰ CỐ TỪ COMPANY (ONLINE / ĐỔI KTV / CỬ ĐỘI TRỰC TIẾP)
    // =========================================================================
    public async Task<List<ManagerIncidentResponse>> GetIncidentsAsync(string? status, CancellationToken ct = default)
    {
        var query = db.EquipmentIncidents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<EquipmentIncidentStatus>(status.Replace("_", ""), true, out var st))
        {
            query = query.Where(i => i.Status == st);
        }

        var list = await query.OrderByDescending(i => i.ReportedAt).ToListAsync(ct);
        var ids = list.Select(i => i.Id).ToList();

        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);

        var remoteTechIds = list.Where(i => i.RemoteTechnicianStaffId != null)
            .Select(i => i.RemoteTechnicianStaffId!.Value).Distinct().ToList();

        var techNames = await (
            from s in db.Staff.AsNoTracking()
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where remoteTechIds.Contains(s.Id)
            select new { s.Id, u.FullName }
        ).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);

        var items = await (
            from ii in db.EquipmentIncidentItems.AsNoTracking()
            join e in db.Equipments.AsNoTracking() on ii.EquipmentId equals e.Id
            where ids.Contains(ii.IncidentId)
            select new { ii.IncidentId, e.SerialNumber }
        ).ToListAsync(ct);

        var attachments = await (
            from a in db.EquipmentIncidentAttachments.AsNoTracking()
            join f in db.StorageFiles.AsNoTracking() on a.StorageFileId equals f.Id
            where ids.Contains(a.IncidentId)
            select new { a.IncidentId, f }
        ).ToListAsync(ct);

        return list.Select(i =>
        {
            var urls = attachments.Where(a => a.IncidentId == i.Id)
                .Select(a => fileUploadService.GetPrivateImageUrl(
                    $"{a.f.BucketName}/{a.f.ObjectKey}",
                    a.f.ResourceType.ToCloudinaryNetResourceType(),
                    a.f.ContentType ?? "image/jpeg"))
                .ToList();

            return new ManagerIncidentResponse(
                i.Id, i.IncidentCode, i.CompanyId,
                companies.GetValueOrDefault(i.CompanyId)?.Name ?? "",
                i.ContactName, i.ContactPhone, i.Description,
                EnumName.ToDatabaseValue(i.Status), i.ReportedAt,
                i.RemoteTechnicianStaffId,
                i.RemoteTechnicianStaffId.HasValue ? techNames.GetValueOrDefault(i.RemoteTechnicianStaffId.Value) : null,
                i.ServiceAssignmentId,
                items.Where(x => x.IncidentId == i.Id).Select(x => x.SerialNumber).ToList(),
                urls,
                i.Resolution);
        }).ToList();
    }

    /// <summary>Giao hỗ trợ Online (không check trùng lịch) HOẶC đổi sang KTV khác nếu không liên hệ được.</summary>
    public async Task AssignOrReassignRemoteIncidentAsync(
        Guid incidentId, AssignRemoteIncidentRequest req, Guid managerId, CancellationToken ct = default)
    {
        var incident = await db.EquipmentIncidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw new AppException("INCIDENT_NOT_FOUND", "Không tìm thấy sự cố.", StatusCodes.Status404NotFound);

        if (incident.Status is not (EquipmentIncidentStatus.Open or EquipmentIncidentStatus.RemoteSupport))
            throw new AppException("INVALID_STATUS", "Chỉ có thể giao hoặc đổi KTV Online khi sự cố đang Mở hoặc Đang hỗ trợ Online.", StatusCodes.Status409Conflict);

        var staff = await db.Staff.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == req.TechnicianStaffId && s.Status == StaffStatus.Active, ct)
            ?? throw new AppException("TECH_NOT_FOUND", "Kỹ thuật viên không tồn tại hoặc đã ngừng hoạt động.", StatusCodes.Status404NotFound);

        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == incident.CompanyId, ct);

        incident.RemoteTechnicianStaffId = staff.Id;
        incident.Status = EquipmentIncidentStatus.RemoteSupport;
        incident.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        await notificationDispatcher.NotifyUsersAsync(
            [staff.UserId],
            $"Hỗ trợ Online sự cố {incident.IncidentCode}",
            $"KH: {company?.Name ?? ""} | Liên hệ: {incident.ContactName ?? "Khách hàng"} ({incident.ContactPhone ?? "xem chi tiết"}). {(string.IsNullOrWhiteSpace(req.Note) ? incident.Description : req.Note.Trim())}",
            "INCIDENT_REMOTE",
            cancellationToken: ct);
    }

    /// <summary>Cử đội tới hiện trường (từ OPEN hoặc chuyển hướng từ REMOTE_SUPPORT) — CHECK khung 08:00–18:00, check trùng lịch, chọn 1 Đội trưởng.</summary>
    public async Task DispatchIncidentTeamAsync(
        Guid incidentId, DispatchIncidentTeamRequest req, Guid managerId, CancellationToken ct = default)
    {
        EnsureWithinDispatchHours(req.ScheduledStartAt);

        var incident = await db.EquipmentIncidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw new AppException("INCIDENT_NOT_FOUND", "Không tìm thấy sự cố.", StatusCodes.Status404NotFound);

        if (incident.Status is not (EquipmentIncidentStatus.Open or EquipmentIncidentStatus.RemoteSupport))
            throw new AppException("INVALID_STATUS", "Sự cố này không ở trạng thái có thể cử đội trực tiếp.", StatusCodes.Status409Conflict);

        var allStaffIds = req.MemberStaffIds.Append(req.LeaderStaffId).Distinct().ToList();
        await EnsureNoTechnicianConflictAsync(allStaffIds, req.ScheduledStartAt, req.EstimatedDurationMinutes, excludeAssignmentId: null, ct);

        var now = DateTimeOffset.UtcNow;
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == incident.CompanyId, ct);

        var assignment = new ServiceAssignment
        {
            Id = Guid.CreateVersion7(),
            Type = "INCIDENT",
            Title = $"Xử lý sự cố {incident.IncidentCode} - {company?.Name ?? ""}",
            Description = req.Note?.Trim() ?? incident.Description,
            Status = ServiceAssignmentStatus.Pending,
            AssignedAt = now,
            ScheduledStartAt = req.ScheduledStartAt.ToUniversalTime(),
            EstimatedDurationMinutes = req.EstimatedDurationMinutes,
            CreatedBy = managerId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ServiceAssignments.Add(assignment);

        foreach (var staffId in allStaffIds)
        {
            db.ServiceAssignmentMembers.Add(new ServiceAssignmentMember
            {
                Id = Guid.CreateVersion7(),
                ServiceAssignmentId = assignment.Id,
                StaffId = staffId,
                Role = staffId == req.LeaderStaffId ? "LEADER" : "MEMBER"
            });
        }

        incident.ServiceAssignmentId = assignment.Id;
        incident.Status = EquipmentIncidentStatus.Dispatching;
        incident.UpdatedAt = now;

        var vnStart = req.ScheduledStartAt.ToOffset(VietnamOffset);
        await NotifyAssignedTeamAsync(
            allStaffIds,
            req.LeaderStaffId,
            assignment.Title,
            $"Khởi hành {vnStart:HH:mm dd/MM/yyyy} ({req.EstimatedDurationMinutes}p) | Liên hệ: {incident.ContactName ?? ""} {incident.ContactPhone ?? ""}",
            "INCIDENT_DISPATCH",
            ct);

        await db.SaveChangesAsync(ct);
    }

    // =========================================================================
    // 5. DUYỆT CÁC YÊU CẦU TỪ NHÂN VIÊN KHÁC
    // =========================================================================
    public async Task<List<PendingReplacementResponse>> GetPendingReplacementsAsync(
        Guid managerId,
        CancellationToken ct = default)
    {
        // Chỉ hiển thị đề xuất đổi thiết bị thuộc các hợp đồng do Manager này phụ trách (ApprovedBy == managerId)
        var reqs = await (
            from r in db.ReplacementRequests.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on r.RentalContractId equals c.Id
            where r.Status == ReplacementRequestStatus.Pending && c.ApprovedBy == managerId
            orderby r.CreatedAt descending
            select r
        ).ToListAsync(ct);
        var ids = reqs.Select(r => r.Id).ToList();

        var items = await (
            from ri in db.ReplacementRequestItems.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on ri.OldEquipmentId equals eq.Id
            join oldM in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals oldM.Id
            join newM in db.EquipmentModels.AsNoTracking() on ri.ReplacementModelId equals newM.Id into nm
            from newModel in nm.DefaultIfEmpty()
            where ids.Contains(ri.ReplacementRequestId)
            select new
            {
                ri.ReplacementRequestId,
                Dto = new PendingReplacementItemDto(
                    ri.OldEquipmentId, eq.SerialNumber, oldM.Name,
                    ri.ReplacementModelId, newModel != null ? newModel.Name : null, ri.Note)
            }
        ).ToListAsync(ct);

        var contracts = await (
            from c in db.RentalContracts.AsNoTracking()
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            select new { c.Id, c.ContractCode, CompanyName = co.Name }
        ).ToDictionaryAsync(x => x.Id, ct);

        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return reqs.Select(r =>
        {
            contracts.TryGetValue(r.RentalContractId, out var c);
            return new PendingReplacementResponse(
                r.Id, r.RequestCode, c?.ContractCode ?? "", c?.CompanyName ?? "",
                r.Reason, EnumName.ToDatabaseValue(r.Status),
                users.GetValueOrDefault(r.RequestedBy, ""), r.CreatedAt,
                items.Where(x => x.ReplacementRequestId == r.Id).Select(x => x.Dto).ToList());
        }).ToList();
    }

    public async Task DecideReplacementAsync(Guid id, ApprovalDecisionRequest req, Guid managerId, CancellationToken ct = default)
    {
        var entity = await db.ReplacementRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new AppException("NOT_FOUND", "Không tìm thấy yêu cầu đổi thiết bị.", StatusCodes.Status404NotFound);

        if (entity.Status != ReplacementRequestStatus.Pending)
            throw new AppException("INVALID_STATUS", "Yêu cầu này đã được xử lý.", StatusCodes.Status409Conflict);

        var contractOwner = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == entity.RentalContractId)
            .Select(c => new { c.ContractCode, c.ApprovedBy })
            .FirstOrDefaultAsync(ct);

        if (contractOwner is not null && contractOwner.ApprovedBy.HasValue && contractOwner.ApprovedBy.Value != managerId)
        {
            throw new AppException(
                "FORBIDDEN_MANAGER",
                $"Hợp đồng {contractOwner.ContractCode} do Quản lý khác phụ trách, bạn không có quyền duyệt yêu cầu đổi thiết bị này.",
                StatusCodes.Status403Forbidden);
        }

        entity.Status = req.Approve ? ReplacementRequestStatus.Approved : ReplacementRequestStatus.Rejected;
        entity.ApprovedBy = managerId;
        entity.ApprovedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<PendingHandoverResponse>> GetPendingHandoversAsync(Guid managerId, CancellationToken ct = default)
    {
        var notes = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where h.Status == HandoverNoteStatus.PendingApproval && c.ApprovedBy == managerId
            orderby h.HandoverDate
            select new { h, c.ContractCode, CompanyName = co.Name }
        ).ToListAsync(ct);

        var handoverIds = notes.Select(x => x.h.Id).ToList();
        var contractIds = notes.Select(x => x.h.RentalContractId).Distinct().ToList();

        var requiredItems = await (
            from ci in db.RentalContractItems.AsNoTracking()
            join m in db.EquipmentModels.AsNoTracking() on ci.EquipmentModelId equals m.Id
            where contractIds.Contains(ci.RentalContractId)
            select new { ci.RentalContractId, ci.EquipmentModelId, m.Code, m.Name, ci.Quantity, Label = $"{m.Code} x{ci.Quantity}" }
        ).ToListAsync(ct);

        var preparedItems = await (
            from hi in db.HandoverItems.AsNoTracking()
            join e in db.Equipments.AsNoTracking() on hi.EquipmentId equals e.Id
            join m in db.EquipmentModels.AsNoTracking() on e.EquipmentModelId equals m.Id
            where handoverIds.Contains(hi.HandoverNoteId)
            orderby m.Name, e.SerialNumber
            select new
            {
                hi.HandoverNoteId,
                e.Id,
                e.SerialNumber,
                e.EquipmentModelId,
                ModelCode = m.Code,
                ModelName = m.Name
            }
        ).ToListAsync(ct);

        var subMap = await (
            from req in db.ReplacementRequests.AsNoTracking()
            join ri in db.ReplacementRequestItems.AsNoTracking() on req.Id equals ri.ReplacementRequestId
            join targetM in db.EquipmentModels.AsNoTracking() on ri.ReplacementModelId equals targetM.Id
            where req.HandoverId != null
                  && handoverIds.Contains(req.HandoverId.Value)
                  && req.Status != ReplacementRequestStatus.Cancelled
                  && req.Status != ReplacementRequestStatus.Rejected
            select new
            {
                HandoverId = req.HandoverId!.Value,
                ri.OldEquipmentId,
                TargetModelId = targetM.Id,
                TargetLabel = $"{targetM.Code} ({targetM.Name})"
            }
        ).ToListAsync(ct);

        return notes.Select(x =>
        {
            var contractReqs = requiredItems.Where(r => r.RentalContractId == x.h.RentalContractId).ToList();
            var reqByModelId = contractReqs.ToDictionary(r => r.EquipmentModelId);
            var prep = preparedItems.Where(p => p.HandoverNoteId == x.h.Id).ToList();
            var subByEquip = subMap.Where(s => s.HandoverId == x.h.Id).ToDictionary(s => s.OldEquipmentId);

            var groups = prep
                .GroupBy(p => new { p.EquipmentModelId, p.ModelCode, p.ModelName })
                .Select(g =>
                {
                    var hasDirectReq = reqByModelId.TryGetValue(g.Key.EquipmentModelId, out var directReq);
                    var subLabels = g
                        .Select(item => subByEquip.TryGetValue(item.Id, out var s) ? s.TargetLabel : null)
                        .Where(l => l != null)
                        .Distinct()
                        .ToList();

                    var isSub = !hasDirectReq || subLabels.Count > 0;
                    return new PendingHandoverModelGroupDto(
                        g.Key.EquipmentModelId,
                        g.Key.ModelCode,
                        g.Key.ModelName,
                        g.Count(),
                        directReq?.Quantity,
                        isSub,
                        subLabels.Count > 0 ? string.Join(", ", subLabels) : null,
                        g.Select(i => i.SerialNumber).ToList());
                })
                .ToList();

            var flatDtos = prep.Select(p =>
            {
                subByEquip.TryGetValue(p.Id, out var sub);
                return new PendingHandoverItemDto(p.Id, p.SerialNumber, p.ModelCode, p.ModelName, sub?.TargetLabel);
            }).ToList();

            var mismatch = prep.Any(p => !reqByModelId.ContainsKey(p.EquipmentModelId));

            return new PendingHandoverResponse(
                x.h.Id,
                x.h.HandoverCode,
                x.ContractCode,
                x.CompanyName,
                x.h.HandoverDate,
                EnumName.ToDatabaseValue(x.h.Status),
                prep.Count,
                contractReqs.Sum(r => r.Quantity),
                contractReqs.Select(r => r.Label).ToList(),
                groups,
                flatDtos,
                mismatch);
        }).ToList();
    }

    public async Task DecideHandoverAsync(Guid handoverId, ApprovalDecisionRequest req, Guid managerId, CancellationToken ct = default)
    {
        var note = await db.HandoverNotes.FirstOrDefaultAsync(h => h.Id == handoverId, ct)
            ?? throw new AppException("NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);

        if (note.Status != HandoverNoteStatus.PendingApproval)
            throw new AppException("INVALID_STATUS", "Phiếu bàn giao không ở trạng thái chờ duyệt.", StatusCodes.Status409Conflict);

        var now = DateTimeOffset.UtcNow;
        note.Status = req.Approve ? HandoverNoteStatus.Shipping : HandoverNoteStatus.Preparing;
        note.ApprovalNote = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        note.UpdatedAt = now;

        var linkedSubs = await db.ReplacementRequests
            .Where(r => r.HandoverId == handoverId && r.Status == ReplacementRequestStatus.Pending)
            .ToListAsync(ct);

        foreach (var sub in linkedSubs)
        {
            sub.Status = req.Approve ? ReplacementRequestStatus.Approved : ReplacementRequestStatus.Rejected;
            sub.ApprovedBy = managerId;
            sub.ApprovedAt = now;
            sub.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        var contractCode = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == note.RentalContractId)
            .Select(c => c.ContractCode)
            .FirstOrDefaultAsync(ct) ?? "";

        var storekeeperRecipient = note.AssignedStorekeeperId ?? note.CreatedBy;
        if (req.Approve)
        {
            await notificationDispatcher.NotifyUsersAsync(
                [storekeeperRecipient],
                $"Phiếu bàn giao đã duyệt xuất kho ({note.HandoverCode})",
                $"Quản lý đã duyệt xuất kho phiếu {note.HandoverCode} (hợp đồng {contractCode}). Thiết bị đã sẵn sàng bàn giao cho đội kỹ thuật.",
                "HANDOVER_APPROVED",
                cancellationToken: ct);

            if (note.ServiceAssignmentId.HasValue)
            {
                var teamUserIds = await (
                    from m in db.ServiceAssignmentMembers.AsNoTracking()
                    join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
                    where m.ServiceAssignmentId == note.ServiceAssignmentId.Value
                    select s.UserId
                ).Distinct().ToListAsync(ct);

                if (teamUserIds.Count > 0)
                {
                    await notificationDispatcher.NotifyUsersAsync(
                        teamUserIds,
                        $"Đã duyệt xuất kho — Sẵn sàng xuất phát ({note.HandoverCode})",
                        $"Phiếu bàn giao {note.HandoverCode} (hợp đồng {contractCode}) đã được Quản lý duyệt xuất kho (SHIPPING). KTV trưởng có thể bấm Bắt đầu xuất phát khi đủ phiếu.",
                        "HANDOVER_READY_TO_SHIP",
                        cancellationToken: ct);
                }
            }
        }
        else
        {
            var rejectReason = string.IsNullOrWhiteSpace(note.ApprovalNote) ? "Vui lòng kiểm tra lại danh sách thiết bị." : note.ApprovalNote;
            await notificationDispatcher.NotifyUsersAsync(
                [storekeeperRecipient],
                $"Phiếu bàn giao bị trả lại ({note.HandoverCode})",
                $"Quản lý đã trả lại phiếu {note.HandoverCode} (hợp đồng {contractCode}) về trạng thái Đang soạn. Lý do: {rejectReason}",
                "HANDOVER_REJECTED",
                cancellationToken: ct);
        }
    }

    public async Task RecordDeliveryFailureAsync(
        Guid handoverId,
        RecordDeliveryFailureRequest req,
        Guid managerId,
        CancellationToken ct = default)
    {
        var note = await db.HandoverNotes.FirstOrDefaultAsync(h => h.Id == handoverId, ct)
            ?? throw new AppException("NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);

        var contract = await db.RentalContracts.FirstOrDefaultAsync(c => c.Id == note.RentalContractId, ct)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.Add(VietnamOffset).Date);
        var reason = req.Reason.Trim();

        // Phiếu giao bù (SUPPLEMENTARY) miễn phí vận chuyển và không tính lượt giao thất bại / không phạt khách
        if (string.Equals(note.HandoverType, "SUPPLEMENTARY", StringComparison.OrdinalIgnoreCase))
        {
            note.FailedReason = reason;
            note.FailedAt = now;
            note.Note = string.IsNullOrWhiteSpace(note.Note)
                ? $"[Giao bù thất bại]: {reason}"
                : $"{note.Note} [Giao bù thất bại]: {reason}";
            if (req.Reschedule)
            {
                note.Status = HandoverNoteStatus.Preparing;
                note.ServiceAssignmentId = null;
            }
            note.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        note.DeliveryAttemptNumber = note.FailedAt.HasValue
            ? Math.Max(1, note.DeliveryAttemptNumber) + 1
            : Math.Max(1, note.DeliveryAttemptNumber);
        note.FailedReason = reason;
        note.FailedAt = now;
        note.Note = string.IsNullOrWhiteSpace(note.Note)
            ? $"Giao thất bại lần {note.DeliveryAttemptNumber}: {reason}"
            : $"{note.Note} Giao thất bại lần {note.DeliveryAttemptNumber}: {reason}";

        if (req.Reschedule)
        {
            note.Status = HandoverNoteStatus.Preparing;
            note.ServiceAssignmentId = null;
        }
        note.UpdatedAt = now;

        if (note.DeliveryAttemptNumber > 3)
        {
            var penaltyFee = Math.Max(0m, req.PenaltyAmount ?? 200000m);
            if (penaltyFee > 0m)
            {
                db.RentalCharges.Add(new RentalCharge
                {
                    Id = Guid.CreateVersion7(),
                    SourceType = "CONTRACT",
                    RentalContractId = contract.Id,
                    ChargeType = "TRANSPORT",
                    Description = $"Phí phạt giao hàng thất bại lần {note.DeliveryAttemptNumber} (vượt quá 3 lần miễn phạt) - {contract.ContractCode}: {reason}",
                    Quantity = 1,
                    UnitPrice = penaltyFee,
                    Amount = penaltyFee,
                    DueDate = today.AddDays(3),
                    PaidAmount = 0m,
                    Status = RentalChargeStatus.Unpaid,
                    CreatedBy = managerId,
                    CreatedAt = now,
                    Note = reason,
                });
            }

            var repEmail = await db.RentalContractRepresentatives
                .AsNoTracking()
                .Where(r => r.RentalContractId == contract.Id && r.Email != null)
                .Select(r => r.Email!)
                .FirstOrDefaultAsync(ct);

            var msg = $"Đơn giao hàng {note.HandoverCode} (Hợp đồng {contract.ContractCode}) giao thất bại lần thứ {note.DeliveryAttemptNumber}."
                      + $"Lý do: {reason}"
                      + $"Do đã vượt quá 3 lần giao lại miễn phí, hệ thống đã lập khoản phụ phí/phạt giao lại: {penaltyFee:N0} đ (hạn thanh toán {today.AddDays(3):dd/MM/yyyy}).";

            await notificationDispatcher.NotifyCompanyAsync(
                contract.CompanyId,
                $"Phí phạt giao hàng thất bại lần {note.DeliveryAttemptNumber} ({contract.ContractCode})",
                msg,
                "DELIVERY_FAILED_PENALTY",
                extraEmails: repEmail is not null ? [repEmail] : null,
                cancellationToken: ct);

            await notificationDispatcher.NotifyUsersAsync(
                [contract.CreatedBy],
                $"Giao hàng thất bại lần {note.DeliveryAttemptNumber} ({contract.ContractCode})",
                msg,
                "DELIVERY_FAILED",
                cancellationToken: ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<PendingDisposalResponse>> GetPendingDisposalsAsync(CancellationToken ct = default)
    {
        return await (
            from d in db.EquipmentDisposals.AsNoTracking()
            join e in db.Equipments.AsNoTracking() on d.EquipmentId equals e.Id
            join m in db.EquipmentModels.AsNoTracking() on e.EquipmentModelId equals m.Id
            join u in db.Users.AsNoTracking() on d.RequestedBy equals u.Id into ug
            from requester in ug.DefaultIfEmpty()
            where d.Status == "PENDING"
            orderby d.CreatedAt descending
            select new PendingDisposalResponse(
                d.Id, d.EquipmentId, e.SerialNumber, m.Name,
                d.Reason, d.Method, d.Amount, d.DisposalDate, d.Status,
                requester != null ? requester.FullName : null)
        ).ToListAsync(ct);
    }

    public async Task DecideDisposalAsync(Guid disposalId, ApprovalDecisionRequest req, Guid managerId, CancellationToken ct = default)
    {
        var disposal = await db.EquipmentDisposals.FirstOrDefaultAsync(d => d.Id == disposalId, ct)
            ?? throw new AppException("NOT_FOUND", "Không tìm thấy đề xuất thanh lý.", StatusCodes.Status404NotFound);

        if (disposal.Status != "PENDING")
            throw new AppException("INVALID_STATUS", "Đề xuất thanh lý đã được xử lý.", StatusCodes.Status409Conflict);

        disposal.Status = req.Approve ? "APPROVED" : "REJECTED";
        disposal.ApprovedBy = managerId;
        disposal.ApprovedAt = DateTimeOffset.UtcNow;
        disposal.RejectionReason = req.Approve ? null : req.Reason?.Trim();

        if (req.Approve)
        {
            var eq = await db.Equipments.FirstOrDefaultAsync(e => e.Id == disposal.EquipmentId, ct);
            if (eq is not null)
            {
                eq.Status = EquipmentStatus.Disposed;
                eq.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================
    private static void EnsureWithinDispatchHours(DateTimeOffset scheduledStartAt)
    {
        var vnTime = TimeOnly.FromDateTime(scheduledStartAt.ToOffset(VietnamOffset).DateTime);
        if (vnTime < DispatchOpenTime || vnTime > DispatchCloseTime)
        {
            throw new AppException(
                "OUTSIDE_DISPATCH_HOURS",
                "Chỉ được cử đội xuất phát trong khung giờ làm việc từ 08:00 đến 18:00.",
                StatusCodes.Status400BadRequest,
                "scheduledStartAt");
        }
    }

    private async Task EnsureNoTechnicianConflictAsync(
        List<Guid> staffIds,
        DateTimeOffset startAt,
        int durationMinutes,
        Guid? excludeAssignmentId,
        CancellationToken ct)
    {
        var avail = await GetTechniciansAvailabilityAsync(startAt, durationMinutes, excludeAssignmentId, ct);
        var busy = avail.Where(t => staffIds.Contains(t.StaffId) && !t.IsAvailable).ToList();
        if (busy.Count > 0)
        {
            var names = string.Join(", ", busy.Select(b =>
                $"{b.FullName} (bận {b.ConflictStart!.Value.ToOffset(VietnamOffset):HH:mm}-{b.ConflictEnd!.Value.ToOffset(VietnamOffset):HH:mm})"));
            throw new AppException(
                "TECHNICIAN_SCHEDULE_CONFLICT",
                $"Kỹ thuật viên bị trùng lịch: {names}",
                StatusCodes.Status409Conflict);
        }
    }

    private async Task NotifyAssignedTeamAsync(
        List<Guid> allStaffIds,
        Guid leaderStaffId,
        string title,
        string content,
        string type,
        CancellationToken ct)
    {
        var staffUsers = await db.Staff.AsNoTracking()
            .Where(s => allStaffIds.Contains(s.Id))
            .Select(s => new { s.Id, s.UserId })
            .ToListAsync(ct);

        var leaderUserIds = staffUsers.Where(s => s.Id == leaderStaffId).Select(s => s.UserId).ToList();
        var memberUserIds = staffUsers.Where(s => s.Id != leaderStaffId).Select(s => s.UserId).ToList();

        if (leaderUserIds.Count > 0)
        {
            await notificationDispatcher.NotifyUsersAsync(
                leaderUserIds,
                $"[Đội trưởng] {title}",
                content,
                type,
                cancellationToken: ct);
        }

        if (memberUserIds.Count > 0)
        {
            await notificationDispatcher.NotifyUsersAsync(
                memberUserIds,
                $"[Thành viên] {title}",
                content,
                type,
                cancellationToken: ct);
        }
    }

    private static string GenerateHandoverCode()
        => $"BG-{DateTime.UtcNow.Add(VietnamOffset):yyyyMMdd}-{Guid.NewGuid().ToString("N")[..5].ToUpperInvariant()}";

    // =========================================================================
    // 6. DUYỆT PHẠT / BỒI THƯỜNG SỰ CỐ (BƯỚC 2) & ĐIỀU KIỆN GIAO THAY THẾ (BƯỚC 3)
    // =========================================================================

    private sealed record ReadyIncidentReplacementInfo(Guid IncidentId, string IncidentCode);

    private async Task<Dictionary<Guid, ReadyIncidentReplacementInfo>> GetReadyIncidentReplacementsForManagerAsync(
        Guid managerId,
        CancellationToken ct)
    {
        var openIncidents = await db.EquipmentIncidents.AsNoTracking()
            .Where(i => i.Status == EquipmentIncidentStatus.ReturnRequired)
            .ToListAsync(ct);

        if (openIncidents.Count == 0) return [];

        var incIds = openIncidents.Select(i => i.Id).ToList();
        var incItems = await db.EquipmentIncidentItems.AsNoTracking()
            .Where(ii => incIds.Contains(ii.IncidentId))
            .ToListAsync(ct);

        var eqIds = incItems.Select(ii => ii.EquipmentId).Distinct().ToList();
        if (eqIds.Count == 0) return [];

        var eqContracts = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
            where eqIds.Contains(hi.EquipmentId) && c.ApprovedBy == managerId && c.Status == RentalContractStatus.Delivered
            orderby hn.CreatedAt descending
            select new { hi.EquipmentId, ContractId = c.Id }
        ).ToListAsync(ct);
        var contractByEq = eqContracts.GroupBy(x => x.EquipmentId).ToDictionary(g => g.Key, g => g.First().ContractId);

        var itemIds = incItems.Select(ii => ii.Id).ToList();
        var chargesByItem = (await db.IncidentCharges.AsNoTracking()
            .Where(ic => itemIds.Contains(ic.IncidentItemId) && ic.Status != IncidentChargeStatus.Cancelled)
            .ToListAsync(ct))
            .GroupBy(ic => ic.IncidentItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var contractIds = contractByEq.Values.Distinct().ToList();
        var pendingIncReturns = (await db.EquipmentReturns.AsNoTracking()
            .Where(r => contractIds.Contains(r.RentalContractId)
                        && r.ReturnType == "INCIDENT_RETURN"
                        && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
            .Select(r => r.RentalContractId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        var result = new Dictionary<Guid, ReadyIncidentReplacementInfo>();

        foreach (var inc in openIncidents)
        {
            var itemsOfInc = incItems.Where(x => x.IncidentId == inc.Id).ToList();
            if (itemsOfInc.Count == 0) continue;

            Guid? matchedContractId = null;
            foreach (var it in itemsOfInc)
            {
                if (contractByEq.TryGetValue(it.EquipmentId, out var cid))
                {
                    matchedContractId = cid;
                    break;
                }
            }
            if (!matchedContractId.HasValue) continue;
            if (pendingIncReturns.Contains(matchedContractId.Value)) continue;

            // Yêu cầu tất cả thiết bị sự cố đã có phân định lỗi (FaultType != null) và không còn khoản phí chưa duyệt / chưa đóng đủ
            var allEvaluated = itemsOfInc.All(it => !string.IsNullOrWhiteSpace(it.FaultType));
            if (!allEvaluated) continue;

            var hasUnsettledCharge = itemsOfInc.Any(it =>
                chargesByItem.TryGetValue(it.Id, out var chs) &&
                chs.Any(ch => ch.Status is IncidentChargeStatus.PendingApproval or IncidentChargeStatus.Pending));
            if (hasUnsettledCharge) continue;

            result.TryAdd(matchedContractId.Value, new ReadyIncidentReplacementInfo(inc.Id, inc.IncidentCode));
        }

        return result;
    }

    public async Task<List<PendingIncidentChargeResponse>> GetPendingIncidentChargesAsync(
        Guid managerId,
        CancellationToken ct = default)
    {
        var raw = await (
            from ic in db.IncidentCharges.AsNoTracking()
            join ii in db.EquipmentIncidentItems.AsNoTracking() on ic.IncidentItemId equals ii.Id
            join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
            join eq in db.Equipments.AsNoTracking() on ii.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            join co in db.Companies.AsNoTracking() on inc.CompanyId equals co.Id
            where ic.Status == IncidentChargeStatus.PendingApproval
            orderby ic.CreatedAt descending
            select new { ic, ii, inc, eq, m, CompanyName = co.Name }
        ).ToListAsync(ct);

        if (raw.Count == 0) return [];

        var eqIds = raw.Select(x => x.eq.Id).Distinct().ToList();
        var handoverContracts = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
            where eqIds.Contains(hi.EquipmentId)
            orderby hn.CreatedAt descending
            select new
            {
                hi.EquipmentId,
                c.Id,
                c.ContractCode,
                c.CompanyId,
                c.ApprovedBy,
                c.DepositPaidAmount,
                c.DepositAppliedAmount,
                c.DepositRefundedAmount
            }
        ).ToListAsync(ct);

        var proposerIds = raw.Where(x => x.ic.ProposedBy.HasValue).Select(x => x.ic.ProposedBy!.Value).Distinct().ToList();
        var proposerNames = proposerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking()
                .Where(u => proposerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var incIds = raw.Select(x => x.inc.Id).Distinct().ToList();
        var incFiles = await (
            from att in db.EquipmentIncidentAttachments.AsNoTracking()
            join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
            where incIds.Contains(att.IncidentId)
            select new { att.IncidentId, f }
        ).ToListAsync(ct);

        var result = new List<PendingIncidentChargeResponse>();
        foreach (var r in raw)
        {
            var contract = handoverContracts.FirstOrDefault(c => c.EquipmentId == r.eq.Id && c.CompanyId == r.inc.CompanyId)
                           ?? handoverContracts.FirstOrDefault(c => c.EquipmentId == r.eq.Id);
            if (contract is null) continue;
            if (contract.ApprovedBy.HasValue && contract.ApprovedBy.Value != managerId) continue;

            var availDeposit = Math.Max(0m, contract.DepositPaidAmount - contract.DepositAppliedAmount - contract.DepositRefundedAmount);
            var urls = incFiles.Where(a => a.IncidentId == r.inc.Id)
                .Select(a => fileUploadService.GetPrivateImageUrl(
                    $"{a.f.BucketName}/{a.f.ObjectKey}",
                    a.f.ResourceType.ToCloudinaryNetResourceType(),
                    string.IsNullOrWhiteSpace(a.f.ContentType) ? "jpg" : a.f.ContentType))
                .ToList();

            result.Add(new PendingIncidentChargeResponse(
                r.ic.Id,
                r.inc.Id,
                r.inc.IncidentCode,
                r.ii.Id,
                r.eq.Id,
                r.eq.SerialNumber,
                r.m.Code,
                r.m.Name,
                contract.Id,
                contract.ContractCode,
                r.CompanyName,
                r.ic.ChargeType,
                r.ic.Description,
                r.ic.Amount,
                availDeposit,
                r.ic.ProposedBy.HasValue ? proposerNames.GetValueOrDefault(r.ic.ProposedBy.Value) : null,
                r.ii.Assessment,
                urls,
                r.ic.CreatedAt));
        }

        return result;
    }

    public async Task<DecideIncidentChargeResponse> DecideIncidentChargeAsync(
        Guid chargeId,
        DecideIncidentChargeRequest req,
        Guid managerId,
        CancellationToken ct = default)
    {
        var decision = (req.Decision ?? "").Trim().ToUpperInvariant();
        if (decision is not ("APPROVE_OFFSET_DEPOSIT" or "APPROVE_DIRECT_BILLING" or "WAIVE"))
        {
            throw new AppException(
                "INVALID_DECISION",
                "Quyết định phải là APPROVE_OFFSET_DEPOSIT (duyệt & cấn trừ cọc), APPROVE_DIRECT_BILLING (duyệt & thu riêng) hoặc WAIVE (miễn phạt).",
                StatusCodes.Status400BadRequest);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var ic = await db.IncidentCharges.FirstOrDefaultAsync(x => x.Id == chargeId, ct)
            ?? throw new AppException("INCIDENT_CHARGE_NOT_FOUND", "Không tìm thấy đề xuất phí sự cố.", StatusCodes.Status404NotFound);

        if (ic.Status != IncidentChargeStatus.PendingApproval)
        {
            throw new AppException("INVALID_STATUS", "Đề xuất phí sự cố này đã được xử lý trước đó.", StatusCodes.Status409Conflict);
        }

        var itemRow = await (
            from ii in db.EquipmentIncidentItems.AsNoTracking()
            join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
            join eq in db.Equipments.AsNoTracking() on ii.EquipmentId equals eq.Id
            where ii.Id == ic.IncidentItemId
            select new { ii, inc, eq }
        ).SingleAsync(ct);

        var contractId = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
            where hi.EquipmentId == itemRow.eq.Id && c.CompanyId == itemRow.inc.CompanyId
            orderby hn.CreatedAt descending
            select (Guid?)c.Id
        ).FirstOrDefaultAsync(ct)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không xác định được hợp đồng liên kết của thiết bị sự cố.", StatusCodes.Status404NotFound);

        var contract = await db.RentalContracts
            .FromSqlInterpolated($"SELECT * FROM rental_contracts WHERE id = {contractId} FOR UPDATE")
            .SingleAsync(ct);

        if (contract.ApprovedBy.HasValue && contract.ApprovedBy.Value != managerId)
        {
            throw new AppException(
                "FORBIDDEN_MANAGER",
                $"Hợp đồng {contract.ContractCode} do Quản lý khác phụ trách, bạn không có quyền duyệt khoản phạt này.",
                StatusCodes.Status403Forbidden);
        }

        var now = DateTimeOffset.UtcNow;
        var finalAmount = req.AdjustedAmount.HasValue ? req.AdjustedAmount.Value : ic.Amount;
        if (decision != "WAIVE" && finalAmount <= 0)
        {
            throw new AppException("INVALID_AMOUNT", "Số tiền phạt sau điều chỉnh phải lớn hơn 0 đ (hoặc chọn Miễn phạt).", StatusCodes.Status400BadRequest);
        }

        ic.Amount = decision == "WAIVE" ? 0m : finalAmount;
        ic.ApprovedBy = managerId;
        ic.ApprovedAt = now;
        ic.ManagerNote = string.IsNullOrWhiteSpace(req.ManagerNote) ? null : req.ManagerNote.Trim();

        var depositOffset = 0m;
        var customerRemaining = 0m;
        var replacementUnlocked = false;
        string responseMessage;

        if (decision == "WAIVE")
        {
            ic.Status = IncidentChargeStatus.Waived;
            ic.SettlementMethod = "WAIVED";
            ic.DepositOffsetAmount = 0m;
            replacementUnlocked = true;
            responseMessage = $"Đã miễn phạt cho sự cố {itemRow.inc.IncidentCode} ({itemRow.eq.SerialNumber}). Đã mở khóa giao máy thay thế.";
        }
        else if (decision == "APPROVE_OFFSET_DEPOSIT")
        {
            var availableDeposit = Math.Max(0m, contract.DepositPaidAmount - contract.DepositAppliedAmount - contract.DepositRefundedAmount);
            depositOffset = Math.Min(finalAmount, availableDeposit);
            customerRemaining = Math.Max(0m, finalAmount - depositOffset);

            if (depositOffset > 0)
            {
                contract.DepositAppliedAmount += depositOffset;
                contract.UpdatedAt = now;
            }

            ic.SettlementMethod = "OFFSET_DEPOSIT";
            ic.DepositOffsetAmount = depositOffset;

            var rcChargeType = ic.ChargeType == "REPLACEMENT" ? "LOSS" : "DAMAGE";
            var rcNote = customerRemaining == 0m
                ? $"Đã cấn trừ 100% ({depositOffset:N0} đ) từ tiền đặt cọc cho sự cố {itemRow.inc.IncidentCode}."
                : depositOffset > 0m
                    ? $"Đã cấn trừ {depositOffset:N0} đ từ tiền đặt cọc (hết cọc); khách cần thanh toán phần chênh lệch {customerRemaining:N0} đ trước khi nhận máy thay thế."
                    : $"Tiền đặt cọc khả dụng đã hết (0 đ); khách cần thanh toán đủ {customerRemaining:N0} đ trước khi nhận máy thay thế.";

            var rc = new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "INCIDENT",
                RentalContractId = contract.Id,
                EquipmentId = itemRow.eq.Id,
                ChargeType = rcChargeType,
                Description = ic.Description ?? $"Phí sự cố {itemRow.inc.IncidentCode} - {itemRow.eq.SerialNumber}",
                Quantity = 1,
                UnitPrice = finalAmount,
                Amount = finalAmount,
                DueDate = DateOnly.FromDateTime(now.ToOffset(VietnamOffset).Date.AddDays(3)),
                PaidAmount = depositOffset,
                Status = customerRemaining == 0m ? RentalChargeStatus.Paid : (depositOffset > 0m ? RentalChargeStatus.PartiallyPaid : RentalChargeStatus.Unpaid),
                PaidAt = customerRemaining == 0m ? now : null,
                CreatedBy = managerId,
                CreatedAt = now,
                Note = rcNote,
            };
            db.RentalCharges.Add(rc);
            ic.RentalChargeId = rc.Id;

            if (customerRemaining == 0m)
            {
                ic.Status = IncidentChargeStatus.Charged;
                replacementUnlocked = true;
                responseMessage = $"Đã duyệt phí {finalAmount:N0} đ và cấn trừ 100% vào tiền đặt cọc. Đã mở khóa giao máy thay thế cho khách.";
            }
            else
            {
                ic.Status = IncidentChargeStatus.Pending;
                replacementUnlocked = false;
                responseMessage = $"Đã duyệt phí {finalAmount:N0} đ (cấn trừ cọc {depositOffset:N0} đ). Còn thiếu {customerRemaining:N0} đ — tạm khóa giao máy thay thế cho tới khi khách thanh toán đủ phần chênh lệch.";
            }
        }
        else // APPROVE_DIRECT_BILLING
        {
            customerRemaining = finalAmount;
            ic.SettlementMethod = "DIRECT_BILLING";
            ic.DepositOffsetAmount = 0m;
            ic.Status = IncidentChargeStatus.Pending;

            var rcChargeType = ic.ChargeType == "REPLACEMENT" ? "LOSS" : "DAMAGE";
            var rc = new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "INCIDENT",
                RentalContractId = contract.Id,
                EquipmentId = itemRow.eq.Id,
                ChargeType = rcChargeType,
                Description = ic.Description ?? $"Phí sự cố {itemRow.inc.IncidentCode} - {itemRow.eq.SerialNumber}",
                Quantity = 1,
                UnitPrice = finalAmount,
                Amount = finalAmount,
                DueDate = DateOnly.FromDateTime(now.ToOffset(VietnamOffset).Date.AddDays(3)),
                PaidAmount = 0m,
                Status = RentalChargeStatus.Unpaid,
                CreatedBy = managerId,
                CreatedAt = now,
                Note = $"Thu trực tiếp khoản bồi thường sự cố {itemRow.inc.IncidentCode} (giữ nguyên tiền đặt cọc).",
            };
            db.RentalCharges.Add(rc);
            ic.RentalChargeId = rc.Id;
            replacementUnlocked = false;
            responseMessage = $"Đã duyệt thu riêng {finalAmount:N0} đ (giữ nguyên tiền cọc). Tạm khóa giao máy thay thế cho tới khi khách thanh toán.";
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Thông báo tới Khách hàng & Thủ kho
        var custMsg = decision == "WAIVE"
            ? $"Quản lý đã miễn khoản phí cho sự cố {itemRow.inc.IncidentCode} ({itemRow.eq.SerialNumber}). Kho sẽ tiến hành chuẩn bị giao máy thay thế cho quý khách."
            : customerRemaining == 0m
                ? $"Quản lý đã duyệt chi phí sửa chữa/bồi thường {finalAmount:N0} đ cho sự cố {itemRow.inc.IncidentCode} và cấn trừ trực tiếp từ tiền đặt cọc. Kho sẽ chuẩn bị giao máy thay thế cho quý khách."
                : $"Quản lý đã duyệt chi phí sự cố {itemRow.inc.IncidentCode}: {finalAmount:N0} đ{(depositOffset > 0 ? $" (đã cấn trừ cọc {depositOffset:N0} đ)" : "")}. Vui lòng thanh toán phần còn lại {customerRemaining:N0} đ qua VNPAY trên ứng dụng để tiếp tục nhận máy thay thế.";

        await notificationDispatcher.NotifyCompanyAsync(
            itemRow.inc.CompanyId,
            $"Kết quả duyệt chi phí sự cố ({itemRow.inc.IncidentCode})",
            custMsg,
            "INCIDENT_CHARGE_DECIDED",
            cancellationToken: ct);

        if (replacementUnlocked)
        {
            var storekeeperIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "LEAD_STOREKEEPER" || r.Code == "STOREKEEPER"
                select ur.UserId
            ).Distinct().ToListAsync(ct);

            if (storekeeperIds.Count > 0)
            {
                await notificationDispatcher.NotifyUsersAsync(
                    storekeeperIds,
                    $"Mở khóa giao máy thay thế sự cố ({contract.ContractCode})",
                    $"Sự cố {itemRow.inc.IncidentCode} đã hoàn tất xử lý trách nhiệm/tài chính. Thủ kho có thể soạn phiếu giao máy thay thế (INCIDENT_REPLACEMENT) cho hợp đồng {contract.ContractCode}.",
                    "INCIDENT_REPLACEMENT_READY",
                    cancellationToken: ct);
            }
        }

        return new DecideIncidentChargeResponse(
            ic.Id,
            EnumName.ToDatabaseValue(ic.Status),
            ic.SettlementMethod ?? "",
            ic.Amount,
            depositOffset,
            customerRemaining,
            replacementUnlocked,
            responseMessage);
    }


    // =========================================================================
    // 7. CHUYẾN THU HỒI THIẾT BỊ CUỐI KỲ / HẾT HẠN / HỦY HỢP ĐỒNG (RETRIEVAL)
    // =========================================================================

    public async Task<List<PendingRetrievalContractResponse>> GetPendingRetrievalContractsAsync(
        Guid managerId,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Add(VietnamOffset));

        var contracts = await (
            from c in db.RentalContracts.AsNoTracking()
            join co in db.Companies.AsNoTracking() on c.CompanyId equals co.Id
            where c.ApprovedBy == managerId
                  && (c.Status == RentalContractStatus.Delivered
                      || c.Status == RentalContractStatus.Expired
                      || c.Status == RentalContractStatus.PartiallyDelivered
                      || c.Status == RentalContractStatus.CancellationRequested
                      || c.Status == RentalContractStatus.Cancelled)
            select new
            {
                c,
                CompanyName = co.Name,
                Address = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : co.Address
            }
        ).ToListAsync(ct);

        if (contracts.Count == 0) return [];

        var contractIds = contracts.Select(x => x.c.Id).ToList();

        var outCounts = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join eq in db.Equipments.AsNoTracking() on hi.EquipmentId equals eq.Id
            where contractIds.Contains(hn.RentalContractId)
                  && hn.Status == HandoverNoteStatus.Completed
                  && eq.Status == EquipmentStatus.Rented
                  && !(from ret in db.EquipmentReturns
                       join ri in db.EquipmentReturnItems on ret.Id equals ri.EquipmentReturnId
                       where ret.RentalContractId == hn.RentalContractId
                             && ret.Status != EquipmentReturnStatus.Cancelled
                             && ri.EquipmentId == eq.Id
                       select ri.Id).Any()
            group eq.Id by hn.RentalContractId into g
            select new { g.Key, Count = g.Distinct().Count() }
        ).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var activeRetrievalContractIds = (await db.EquipmentReturns.AsNoTracking()
            .Where(r => contractIds.Contains(r.RentalContractId)
                        && r.ServiceAssignmentId != null
                        && (r.Status == EquipmentReturnStatus.Draft || r.Status == EquipmentReturnStatus.Processing))
            .Select(r => r.RentalContractId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        return contracts
            .Where(x => outCounts.GetValueOrDefault(x.c.Id) > 0)
            .Select(x =>
            {
                var daysUntil = x.c.EndDate.DayNumber - today.DayNumber;
                var isOverdue = daysUntil < 0 || x.c.Status == RentalContractStatus.Expired;
                var isEndingSoon = daysUntil >= 0 && daysUntil <= 7;
                var isCancelled = x.c.Status is RentalContractStatus.Cancelled or RentalContractStatus.CancellationRequested;

                return new PendingRetrievalContractResponse(
                    x.c.Id,
                    x.c.ContractCode,
                    x.CompanyName,
                    EnumName.ToDatabaseValue(x.c.Status),
                    x.c.StartDate,
                    x.c.EndDate,
                    x.Address,
                    outCounts.GetValueOrDefault(x.c.Id),
                    daysUntil,
                    isOverdue,
                    isEndingSoon,
                    isCancelled,
                    activeRetrievalContractIds.Contains(x.c.Id));
            })
            .OrderByDescending(x => x.IsOverdue)
            .ThenByDescending(x => x.IsCancelledContract)
            .ThenBy(x => x.DaysUntilEndDate)
            .ToList();
    }

    public async Task<OptimizedTripResponse> PreviewRetrievalTripAsync(
        PreviewRetrievalTripRequest req,
        Guid managerId,
        CancellationToken ct = default)
    {
        EnsureWithinDispatchHours(req.ScheduledStartAt);
        var (depot, waypoints, _, _) = await PrepareRetrievalWaypointsAsync(req.ContractIds, managerId, ct);
        return await routeService.OptimizeRoundTripAsync(depot, waypoints, ct);
    }

    public async Task<DailyScheduleItemResponse> CreateRetrievalTripAsync(
        CreateRetrievalTripRequest req,
        Guid managerId,
        CancellationToken ct = default)
    {
        EnsureWithinDispatchHours(req.ScheduledStartAt);
        var (depot, waypoints, contracts, outByContract) = await PrepareRetrievalWaypointsAsync(req.ContractIds, managerId, ct);
        var route = await routeService.OptimizeRoundTripAsync(depot, waypoints, ct);

        var allStaffIds = req.MemberStaffIds.Append(req.LeaderStaffId).Distinct().ToList();
        await EnsureNoTechnicianConflictAsync(allStaffIds, req.ScheduledStartAt, route.TotalDurationMinutes, excludeAssignmentId: null, ct);

        var now = DateTimeOffset.UtcNow;
        var assignment = new ServiceAssignment
        {
            Id = Guid.CreateVersion7(),
            Type = "RETRIEVAL",
            Title = string.IsNullOrWhiteSpace(req.Title)
                ? $"Thu hồi thiết bị {contracts.Count} điểm ({string.Join(", ", contracts.Select(c => c.ContractCode))})"
                : req.Title.Trim(),
            Description = req.Note?.Trim(),
            Status = ServiceAssignmentStatus.Pending,
            AssignedAt = now,
            ScheduledStartAt = req.ScheduledStartAt.ToUniversalTime(),
            EstimatedDurationMinutes = route.TotalDurationMinutes,
            CreatedBy = managerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ServiceAssignments.Add(assignment);

        foreach (var sid in allStaffIds)
        {
            db.ServiceAssignmentMembers.Add(new ServiceAssignmentMember
            {
                Id = Guid.CreateVersion7(),
                ServiceAssignmentId = assignment.Id,
                StaffId = sid,
                Role = sid == req.LeaderStaffId ? "LEADER" : "MEMBER",
            });
        }

        var prefix = $"TH-{DateTime.UtcNow.Add(VietnamOffset).Year}-";
        var existingCodes = await db.EquipmentReturns.AsNoTracking()
            .Where(r => r.ReturnCode.StartsWith(prefix))
            .Select(r => r.ReturnCode)
            .ToListAsync(ct);
        var seq = existingCodes
            .Select(code => int.TryParse(code[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        var contractMap = contracts.ToDictionary(c => c.Id);

        foreach (var stop in route.Stops)
        {
            var c = contractMap[stop.ReferenceId];
            var eqIds = outByContract[c.Id];
            seq++;

            var ret = new EquipmentReturn
            {
                Id = Guid.CreateVersion7(),
                ReturnCode = $"{prefix}{seq:D4}",
                RentalContractId = c.Id,
                ServiceAssignmentId = assignment.Id,
                StopOrder = stop.StopOrder,
                PickupLatitude = stop.Latitude,
                PickupLongitude = stop.Longitude,
                EstimatedTravelMinutes = stop.TravelMinutesFromPrevious,
                EstimatedUninstallMinutes = stop.InstallMinutes,
                ReturnedAt = req.ScheduledStartAt.ToUniversalTime(),
                ReceivedBy = managerId,
                AssignedStorekeeperId = null,
                Status = EquipmentReturnStatus.Draft,
                ReturnType = "NORMAL",
                Note = $"Chuyến thu hồi #{stop.StopOrder} | Di chuyển ~{stop.TravelMinutesFromPrevious}p ({stop.DistanceKmFromPrevious}km), Tháo dỡ ~{stop.InstallMinutes}p",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.EquipmentReturns.Add(ret);

            foreach (var eqId in eqIds)
            {
                db.EquipmentReturnItems.Add(new EquipmentReturnItem
                {
                    Id = Guid.CreateVersion7(),
                    EquipmentReturnId = ret.Id,
                    EquipmentId = eqId,
                    Status = EquipmentReturnItemStatus.Returned,
                });
            }
        }

        var vnStart = req.ScheduledStartAt.ToOffset(VietnamOffset);
        await NotifyAssignedTeamAsync(
            allStaffIds,
            req.LeaderStaffId,
            assignment.Title,
            $"Khởi hành thu hồi {vnStart:HH:mm dd/MM/yyyy} • {route.Stops.Count} điểm thu hồi • Dự kiến {route.TotalDurationMinutes} phút.",
            "RETRIEVAL_ASSIGNMENT",
            ct);

        await db.SaveChangesAsync(ct);

        foreach (var c in contracts)
        {
            await notificationDispatcher.NotifyCompanyAsync(
                c.CompanyId,
                $"Lịch thu hồi thiết bị hợp đồng {c.ContractCode}",
                $"Đội kỹ thuật EquipRent sẽ đến thu hồi thiết bị của hợp đồng {c.ContractCode} vào khoảng {vnStart:HH:mm dd/MM/yyyy}. Vui lòng chuẩn bị đủ thiết bị để bàn giao.",
                "RETRIEVAL_SCHEDULED",
                cancellationToken: ct);
        }

        var schedule = await GetDailyScheduleAsync(DateOnly.FromDateTime(vnStart.Date), null, ct);
        return schedule.First(s => s.AssignmentId == assignment.Id);
    }

    private async Task<((double Lat, double Lng, string Address) Depot, List<RouteWaypoint> Waypoints, List<RentalContract> Contracts, Dictionary<Guid, List<Guid>> OutstandingByContract)>
        PrepareRetrievalWaypointsAsync(List<Guid> contractIds, Guid managerId, CancellationToken ct)
    {
        var distinctIds = contractIds.Distinct().ToList();
        var contracts = await db.RentalContracts.AsNoTracking()
            .Where(c => distinctIds.Contains(c.Id))
            .ToListAsync(ct);

        if (contracts.Count != distinctIds.Count)
            throw new AppException("CONTRACT_NOT_FOUND", "Có hợp đồng không tồn tại.", StatusCodes.Status404NotFound);

        var outRows = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join eq in db.Equipments.AsNoTracking() on hi.EquipmentId equals eq.Id
            where distinctIds.Contains(hn.RentalContractId)
                  && hn.Status == HandoverNoteStatus.Completed
                  && eq.Status == EquipmentStatus.Rented
                  && !(from ret in db.EquipmentReturns
                       join ri in db.EquipmentReturnItems on ret.Id equals ri.EquipmentReturnId
                       where ret.RentalContractId == hn.RentalContractId
                             && ret.Status != EquipmentReturnStatus.Cancelled
                             && ri.EquipmentId == eq.Id
                       select ri.Id).Any()
            select new { hn.RentalContractId, eq.Id }
        ).Distinct().ToListAsync(ct);

        var outByContract = outRows
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().ToList());

        var alreadyScheduled = (await db.EquipmentReturns.AsNoTracking()
            .Where(r => distinctIds.Contains(r.RentalContractId)
                        && r.ServiceAssignmentId != null
                        && (r.Status == EquipmentReturnStatus.Draft || r.Status == EquipmentReturnStatus.Processing))
            .Select(r => r.RentalContractId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        foreach (var c in contracts)
        {
            if (c.ApprovedBy != managerId)
                throw new AppException("FORBIDDEN_MANAGER", $"Hợp đồng {c.ContractCode} không thuộc quyền phụ trách của bạn.", StatusCodes.Status403Forbidden);

            if (!outByContract.TryGetValue(c.Id, out var eqIds) || eqIds.Count == 0)
                throw new AppException("NO_OUTSTANDING_EQUIPMENT", $"Hợp đồng {c.ContractCode} không còn thiết bị nào đang cho thuê cần thu hồi.", StatusCodes.Status409Conflict);

            if (alreadyScheduled.Contains(c.Id))
                throw new AppException("RETRIEVAL_ALREADY_SCHEDULED", $"Hợp đồng {c.ContractCode} đã có chuyến thu hồi đang thực hiện.", StatusCodes.Status409Conflict);
        }

        var cachedCoords = (await db.HandoverNotes.AsNoTracking()
            .Where(h => distinctIds.Contains(h.RentalContractId) && h.DeliveryLatitude != null && h.DeliveryLongitude != null)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new { h.RentalContractId, Lat = h.DeliveryLatitude!.Value, Lng = h.DeliveryLongitude!.Value })
            .ToListAsync(ct))
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => (g.First().Lat, g.First().Lng));

        var companies = await db.Companies.AsNoTracking()
            .Where(co => contracts.Select(c => c.CompanyId).Contains(co.Id))
            .ToDictionaryAsync(co => co.Id, ct);

        var warehouse = await db.Warehouses
            .Where(w => w.Status == WarehouseStatus.Active)
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("WAREHOUSE_NOT_FOUND", "Hệ thống chưa có kho đang hoạt động.", StatusCodes.Status404NotFound);

        double depotLat, depotLng;
        if (warehouse.Latitude.HasValue && warehouse.Longitude.HasValue)
        {
            depotLat = warehouse.Latitude.Value;
            depotLng = warehouse.Longitude.Value;
        }
        else
        {
            (depotLat, depotLng) = await routeService.ResolveAddressAsync(
                warehouse.Address ?? "TP. Hồ Chí Minh", $"Kho {warehouse.Name}", ct);
            warehouse.Latitude = depotLat;
            warehouse.Longitude = depotLng;
            await db.SaveChangesAsync(ct);
        }

        var depot = (depotLat, depotLng, warehouse.Address ?? warehouse.Name);
        var waypoints = contracts.Select(c =>
        {
            var co = companies[c.CompanyId];
            var addr = !string.IsNullOrWhiteSpace(c.DeliveryAddress) ? c.DeliveryAddress : (co.Address ?? "");
            var hasCache = cachedCoords.TryGetValue(c.Id, out var coord);
            var uninstallMinutes = Math.Max(20, outByContract[c.Id].Count * 10);
            return new RouteWaypoint(
                c.Id,
                $"{c.ContractCode} - {co.Name}",
                addr,
                hasCache ? coord.Lat : null,
                hasCache ? coord.Lng : null,
                uninstallMinutes);
        }).ToList();

        return (depot, waypoints, contracts, outByContract);
    }

}
