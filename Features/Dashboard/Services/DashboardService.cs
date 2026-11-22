using HeThongHoTroChoThueThietBi.Features.Dashboard.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
            c.Status == RentalContractStatus.Preparing ||
            c.Status == RentalContractStatus.PartiallyDelivered ||
            c.Status == RentalContractStatus.Delivered,
            cancellationToken);

        var rejectedContracts = await contracts.CountAsync(c => c.Status == RentalContractStatus.Rejected, cancellationToken);

        var companies = await db.Companies.AsNoTracking().CountAsync(c => c.CreatedBy == salesId, cancellationToken);

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

    private static string ContractEventText(RentalContractStatus status, RentalContractStatus previous) => status switch
    {
        RentalContractStatus.Draft when previous == RentalContractStatus.Draft => "vừa được tạo",
        RentalContractStatus.PendingApproval => "đã gửi duyệt",
        RentalContractStatus.Rejected => "bị từ chối",
        RentalContractStatus.PendingSignature => "đã được duyệt",
        RentalContractStatus.Signed => "đã ký",
        RentalContractStatus.Cancelled => "đã bị hủy",
        RentalContractStatus.Completed => "đã hoàn tất",
        _ => "được cập nhật",
    };
}
