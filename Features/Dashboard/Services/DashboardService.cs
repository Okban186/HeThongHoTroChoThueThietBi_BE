using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Dashboard.DTOs;
using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;

namespace HeThongHoTroChoThueThietBi.Features.Dashboard.Services;

/// <summary>Số liệu tổng hợp cho trang Tổng quan.</summary>
public sealed class DashboardService(AppDbContext db)
{
    private const int RecentActivityLimit = 10;


    public async Task<SalesDashboardResponse> GetSalesDashboardAsync(
        Guid salesId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var reservations = db.Reservations.AsNoTracking().Where(r => r.CreatedBy == salesId);
        var contracts = db.RentalContracts.AsNoTracking().Where(c => c.CreatedBy == salesId);

        var totalReservations = await reservations.CountAsync(cancellationToken);

        var processingReservations = await reservations.CountAsync(r =>
            (r.Status == ReservationStatus.Pending || r.Status == ReservationStatus.Approved) && r.ExpiresAt > now,
            cancellationToken);

        var reservationsToComplete = await reservations.CountAsync(r =>
            r.Status == ReservationStatus.Pending && r.ExpiresAt > now &&
            !db.ReservationItems.Any(i => i.ReservationId == r.Id),
            cancellationToken);

        var activeContracts = await contracts.CountAsync(c =>
            c.Status == RentalContractStatus.Signed ||
            c.Status == RentalContractStatus.PendingDeposit ||
            c.Status == RentalContractStatus.Preparing ||
            c.Status == RentalContractStatus.PartiallyDelivered ||
            c.Status == RentalContractStatus.Delivered,
            cancellationToken);

        var rejectedContracts = await contracts.CountAsync(c => c.Status == RentalContractStatus.Rejected, cancellationToken);

        // Công ty là dữ liệu dùng chung, đếm số công ty đang hoạt động (không theo người tạo).
        var companies = await db.Companies.AsNoTracking().CountAsync(c => c.Status == CompanyStatus.Active, cancellationToken);

        return new SalesDashboardResponse(
            totalReservations,
            processingReservations,
            reservationsToComplete,
            activeContracts,
            rejectedContracts,
            companies,
            await GetRecentActivitiesAsync(salesId, cancellationToken));
    }

    /// <summary>
    /// Hoạt động gần đây: phiếu mình lập, thay đổi trạng thái hợp đồng của mình
    /// (kể cả do Manager duyệt/từ chối), công ty mình tạo.
    /// </summary>
    private async Task<List<SalesActivityResponse>> GetRecentActivitiesAsync(
        Guid salesId,
        CancellationToken cancellationToken)
    {
        var reservationEvents = await (
            from r in db.Reservations.AsNoTracking()
            join company in db.Companies.AsNoTracking() on r.CompanyId equals company.Id
            where r.CreatedBy == salesId
            orderby r.CreatedAt descending
            select new { r.Id, r.ReservationCode, CompanyName = company.Name, r.CreatedAt }
        ).Take(RecentActivityLimit).ToListAsync(cancellationToken);

        var contractEvents = await (
            from h in db.ContractStatusHistory.AsNoTracking()
            join contract in db.RentalContracts.AsNoTracking() on h.RentalContractId equals contract.Id
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where contract.CreatedBy == salesId
            orderby h.ChangedAt descending
            select new { contract.Id, contract.ContractCode, CompanyName = company.Name, h.Status, h.PreviousStatus, h.ChangedAt }
        ).Take(RecentActivityLimit).ToListAsync(cancellationToken);

        var companyEvents = await db.Companies
            .AsNoTracking()
            .Where(c => c.CreatedBy == salesId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(RecentActivityLimit)
            .Select(c => new { c.Id, c.Name, c.CompanyCode, c.CreatedAt })
            .ToListAsync(cancellationToken);

        var activities = new List<SalesActivityResponse>();

        activities.AddRange(reservationEvents.Select(r => new SalesActivityResponse(
            "RESERVATION", r.Id, $"Phiếu {r.ReservationCode} vừa được tạo", r.CompanyName, r.CreatedAt)));

        activities.AddRange(contractEvents.Select(c => new SalesActivityResponse(
            "CONTRACT", c.Id, $"Hợp đồng {c.ContractCode} {ContractEventText(c.Status, c.PreviousStatus)}", c.CompanyName, c.ChangedAt)));

        activities.AddRange(companyEvents.Select(c => new SalesActivityResponse(
            "COMPANY", c.Id, "Thêm khách hàng mới", $"{c.Name} ({c.CompanyCode})", c.CreatedAt)));

        return activities
            .OrderByDescending(a => a.OccurredAt)
            .Take(RecentActivityLimit)
            .ToList();
    }

    public async Task<StorekeeperDashboardResponse> GetStorekeeperDashboardAsync(
        Guid storekeeperUserId,
        CancellationToken ct = default)
    {
        var totalModels = await db.EquipmentModels.CountAsync(ct);
        var activeModels = await db.EquipmentModels.CountAsync(m => m.Status == EquipmentModelStatus.Active, ct);

        var statusCounts = await db.Equipments.AsNoTracking()
            .GroupBy(e => e.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        int CountOf(EquipmentStatus s) => statusCounts.GetValueOrDefault(s);
        var totalEq = statusCounts.Values.Sum();

        var outOfStockModels = await db.EquipmentModels.AsNoTracking()
            .Where(m => m.Status == EquipmentModelStatus.Active &&
                !db.Equipments.Any(e => e.EquipmentModelId == m.Id && e.Status == EquipmentStatus.Available))
            .CountAsync(ct);

        var activeWarehouses = await db.Warehouses.CountAsync(w => w.Status == WarehouseStatus.Active, ct);
        var activeCategories = await db.Categories.CountAsync(c => c.Status == CategoryStatus.Active, ct);

        var recent = await (from e in db.Equipments.AsNoTracking()
                            join m in db.EquipmentModels.AsNoTracking() on e.EquipmentModelId equals m.Id
                            join w in db.Warehouses.AsNoTracking() on e.WarehouseId equals w.Id
                            orderby e.CreatedAt descending
                            select new StorekeeperRecentEquipmentItem(
                                e.Id, e.SerialNumber, m.Name, w.Name,
                                EnumName.ToDatabaseValue(e.Status), e.CreatedAt))
                            .Take(6)
                            .ToListAsync(ct);

        // Thống kê vận hành kho tách bạch theo Thủ kho đang đăng nhập
        var unclaimedHandovers = await db.HandoverNotes.AsNoTracking()
            .CountAsync(h => h.Status == HandoverNoteStatus.Preparing && h.AssignedStorekeeperId == null, ct);

        var myPreparingHandovers = await db.HandoverNotes.AsNoTracking()
            .CountAsync(h => h.Status == HandoverNoteStatus.Preparing && h.AssignedStorekeeperId == storekeeperUserId, ct);

        var myPendingApprovalHandovers = await db.HandoverNotes.AsNoTracking()
            .CountAsync(h => h.Status == HandoverNoteStatus.PendingApproval && h.AssignedStorekeeperId == storekeeperUserId, ct);

        var unclaimedReturns = await db.EquipmentReturns.AsNoTracking()
            .CountAsync(r => r.Status == EquipmentReturnStatus.Processing && r.AssignedStorekeeperId == null, ct);

        var myProcessingReturns = await db.EquipmentReturns.AsNoTracking()
            .CountAsync(r => r.Status == EquipmentReturnStatus.Processing && r.AssignedStorekeeperId == storekeeperUserId, ct);

        // Đề xuất đổi model liên quan đến các phiếu mình phụ trách (hoặc toàn bộ nếu chưa gắn phiếu)
        var myHandoverIds = db.HandoverNotes.AsNoTracking()
            .Where(h => h.AssignedStorekeeperId == storekeeperUserId || h.AssignedStorekeeperId == null)
            .Select(h => h.Id);

        var repStatusCounts = await db.ReplacementRequests.AsNoTracking()
            .Where(r => r.HandoverId == null || myHandoverIds.Contains(r.HandoverId.Value))
            .GroupBy(r => r.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Hợp đồng giao một phần (PARTIALLY_DELIVERED): tách rõ đang chờ thu hồi máy lỗi vs sẵn sàng soạn giao bù
        var partialContracts = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Status == RentalContractStatus.PartiallyDelivered)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var waitingDefReturnContracts = 0;
        var readySuppContracts = 0;
        if (partialContracts.Count > 0)
        {
            var contractsWithPendingDefReturn = (await db.EquipmentReturns.AsNoTracking()
                .Where(r => partialContracts.Contains(r.RentalContractId)
                            && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                            && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
                .Select(r => r.RentalContractId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

            var contractsClaimedByOther = (await db.HandoverNotes.AsNoTracking()
                .Where(h => partialContracts.Contains(h.RentalContractId)
                            && h.HandoverType == "SUPPLEMENTARY"
                            && h.Status != HandoverNoteStatus.Completed
                            && h.Status != HandoverNoteStatus.Cancelled
                            && h.AssignedStorekeeperId != null
                            && h.AssignedStorekeeperId != storekeeperUserId)
                .Select(h => h.RentalContractId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

            waitingDefReturnContracts = contractsWithPendingDefReturn.Count;
            readySuppContracts = partialContracts.Count(id =>
                !contractsWithPendingDefReturn.Contains(id) && !contractsClaimedByOther.Contains(id));
        }

        return new StorekeeperDashboardResponse(
            totalModels, activeModels, totalEq,
            CountOf(EquipmentStatus.Available),
            CountOf(EquipmentStatus.Rented),
            CountOf(EquipmentStatus.Reserved),
            CountOf(EquipmentStatus.Maintenance),
            CountOf(EquipmentStatus.Damaged) + CountOf(EquipmentStatus.Lost),
            outOfStockModels, activeWarehouses, activeCategories, recent,
            unclaimedHandovers,
            myPreparingHandovers,
            myPendingApprovalHandovers,
            unclaimedReturns,
            myProcessingReturns,
            repStatusCounts.GetValueOrDefault(ReplacementRequestStatus.Pending),
            repStatusCounts.GetValueOrDefault(ReplacementRequestStatus.Approved),
            repStatusCounts.GetValueOrDefault(ReplacementRequestStatus.Rejected),
            waitingDefReturnContracts,
            readySuppContracts);
    }

    public async Task<ManagerDashboardResponse> GetManagerDashboardAsync(
    Guid managerId, CancellationToken ct = default)
    {
        var vnOffset = TimeSpan.FromHours(7);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Add(vnOffset));
        var startOfDay = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), vnOffset).ToUniversalTime();
        var endOfDay = startOfDay.AddDays(1);

        var pendingApproval = await db.RentalContracts.AsNoTracking()
            .CountAsync(c => c.Status == RentalContractStatus.PendingApproval, ct);

        var pendingSigned = await db.RentalContracts.AsNoTracking()
            .CountAsync(c => c.Status == RentalContractStatus.Signed && c.ApprovedBy == managerId, ct);

        var pendingDeposit = await db.RentalContracts.AsNoTracking()
            .CountAsync(c => c.Status == RentalContractStatus.PendingDeposit && c.ApprovedBy == managerId, ct);

        var readyToSchedule = await db.RentalContracts.AsNoTracking()
            .CountAsync(c => c.Status == RentalContractStatus.Preparing
                             && c.ApprovedBy == managerId
                             && !db.HandoverNotes.Any(h => h.RentalContractId == c.Id
                                                           && h.ServiceAssignmentId != null
                                                           && h.Status != HandoverNoteStatus.Completed
                                                           && h.Status != HandoverNoteStatus.Cancelled), ct);

        var partialContractIds = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Status == RentalContractStatus.PartiallyDelivered && c.ApprovedBy == managerId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var waitingDefectiveReturnCount = 0;
        var pendingSupplementaryCount = 0;
        if (partialContractIds.Count > 0)
        {
            var waitingDefIds = (await db.EquipmentReturns.AsNoTracking()
                .Where(r => partialContractIds.Contains(r.RentalContractId)
                            && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                            && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
                .Select(r => r.RentalContractId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

            var alreadyScheduledSuppIds = (await db.HandoverNotes.AsNoTracking()
                .Where(h => partialContractIds.Contains(h.RentalContractId)
                            && h.HandoverType == "SUPPLEMENTARY"
                            && h.ServiceAssignmentId != null
                            && h.Status != HandoverNoteStatus.Completed
                            && h.Status != HandoverNoteStatus.Cancelled)
                .Select(h => h.RentalContractId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet();

            waitingDefectiveReturnCount = waitingDefIds.Count;
            pendingSupplementaryCount = partialContractIds.Count(id =>
                !waitingDefIds.Contains(id) && !alreadyScheduledSuppIds.Contains(id));
        }

        var failedDeliveriesCount = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            where c.ApprovedBy == managerId
                  && h.FailedAt != null
                  && h.ServiceAssignmentId == null
                  && h.Status != HandoverNoteStatus.Completed
                  && h.Status != HandoverNoteStatus.Cancelled
            select h.Id
        ).CountAsync(ct);

        var processingReturnsCount = await db.EquipmentReturns.AsNoTracking()
            .CountAsync(r => r.Status == EquipmentReturnStatus.Processing, ct);

        var pendingCashPaymentsCount = await (
            from t in db.PaymentTransactions.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on t.RentalContractId equals c.Id
            where t.PaymentMethod == PaymentMethod.Cash
                  && t.Status == PaymentTransactionStatus.Pending
                  && c.ApprovedBy == managerId
            select t.Id
        ).CountAsync(ct);

        var pendingCancel = await db.RentalContracts.AsNoTracking()
            .CountAsync(c => c.Status == RentalContractStatus.CancellationRequested && c.ApprovedBy == managerId, ct);

        var openIncidents = await db.EquipmentIncidents.AsNoTracking()
            .CountAsync(i => i.Status == EquipmentIncidentStatus.Open || i.Status == EquipmentIncidentStatus.RemoteSupport, ct);

        var pendingReplacements = await (
            from r in db.ReplacementRequests.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on r.RentalContractId equals c.Id
            where r.Status == ReplacementRequestStatus.Pending && c.ApprovedBy == managerId
            select r.Id
        ).CountAsync(ct);

        var pendingHandovers = await (
            from h in db.HandoverNotes.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on h.RentalContractId equals c.Id
            where h.Status == HandoverNoteStatus.PendingApproval && c.ApprovedBy == managerId
            select h.Id
        ).CountAsync(ct);

        var pendingDisposals = await db.EquipmentDisposals.AsNoTracking()
            .CountAsync(d => d.Status == "PENDING", ct);

        var pendingIncidentCharges = await db.IncidentCharges.AsNoTracking()
            .CountAsync(ic => ic.Status == IncidentChargeStatus.PendingApproval, ct);

        var retrievalAlertDate = today.AddDays(7);
        var pendingRetrievalContracts = await db.RentalContracts.AsNoTracking()
            .CountAsync(c => c.ApprovedBy == managerId
                             && (c.Status == RentalContractStatus.Expired
                                 || ((c.Status == RentalContractStatus.Delivered || c.Status == RentalContractStatus.PartiallyDelivered)
                                     && c.EndDate <= retrievalAlertDate))
                             && !db.EquipmentReturns.Any(r => r.RentalContractId == c.Id
                                                              && r.ServiceAssignmentId != null
                                                              && (r.Status == EquipmentReturnStatus.Draft || r.Status == EquipmentReturnStatus.Processing)), ct);

        var todaySchedules = await db.ServiceAssignments.AsNoTracking()
            .CountAsync(a => a.ScheduledStartAt >= startOfDay
                             && a.ScheduledStartAt < endOfDay
                             && a.Status != ServiceAssignmentStatus.Cancelled, ct);

        return new ManagerDashboardResponse(
            pendingApproval,
            pendingSigned,
            readyToSchedule,
            pendingCancel,
            openIncidents,
            pendingReplacements,
            pendingHandovers,
            pendingDisposals,
            todaySchedules,
            pendingSupplementaryCount,
            waitingDefectiveReturnCount,
            failedDeliveriesCount,
            processingReturnsCount,
            pendingCashPaymentsCount,
            pendingDeposit,
            pendingIncidentCharges,
            pendingRetrievalContracts);
    }

    private static string ContractEventText(RentalContractStatus status, RentalContractStatus previous) => status switch
    {
        RentalContractStatus.Draft when previous == RentalContractStatus.Draft => "vừa được tạo",
        RentalContractStatus.PendingApproval => "đã gửi duyệt",
        RentalContractStatus.Rejected => "bị từ chối",
        RentalContractStatus.PendingSignature => "đã được duyệt",
        RentalContractStatus.Signed => "đã ký (chờ duyệt bản ký)",
        RentalContractStatus.PendingDeposit => "đã duyệt bản ký (chờ đặt cọc)",
        RentalContractStatus.Cancelled => "đã bị hủy",
        RentalContractStatus.Completed => "đã hoàn tất",
        _ => "được cập nhật",
    };
}
