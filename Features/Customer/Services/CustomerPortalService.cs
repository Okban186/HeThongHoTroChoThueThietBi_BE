using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Customer.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Customer.Services;

/// <summary>
/// Cổng khách hàng (role COMPANY): mỗi tài khoản thuộc đúng một công ty (company_users),
/// chỉ xem dữ liệu của công ty mình.
/// </summary>
public sealed class CustomerPortalService(AppDbContext db)
{
    private const int EndingSoonDays = 7;

    private static readonly RentalContractStatus[] ActiveStatuses =
    [
        RentalContractStatus.Signed,
        RentalContractStatus.Preparing,
        RentalContractStatus.PartiallyDelivered,
        RentalContractStatus.Delivered,
    ];

    /// <summary>Công ty của tài khoản; tài khoản chưa gắn công ty thì báo lỗi 403.</summary>
    public async Task<Guid> GetCompanyIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var companyId = await db.CompanyUsers
            .AsNoTracking()
            .Where(cu => cu.UserId == userId)
            .Select(cu => (Guid?)cu.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);

        return companyId ?? throw new AppException(
            "COMPANY_NOT_LINKED",
            "Tài khoản chưa được gắn với công ty nào. Vui lòng liên hệ nhân viên kinh doanh.",
            StatusCodes.Status403Forbidden);
    }

    public async Task<CustomerCompanyResponse> GetCompanyAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var companyId = await GetCompanyIdAsync(userId, cancellationToken);

        var row = await (
            from company in db.Companies.AsNoTracking()
            join sales in db.Users.AsNoTracking() on company.CreatedBy equals sales.Id into salesJoin
            from sales in salesJoin.DefaultIfEmpty()
            where company.Id == companyId
            select new
            {
                company,
                SalesName = sales == null ? null : sales.FullName,
                SalesPhone = sales == null ? null : sales.Phone,
                SalesEmail = sales == null ? null : sales.Email,
            }
        ).SingleAsync(cancellationToken);

        return new CustomerCompanyResponse(
            row.company.Id,
            row.company.CompanyCode,
            row.company.Name,
            row.company.TaxCode,
            row.company.Address,
            EnumName.ToDatabaseValue(row.company.Status),
            row.SalesName,
            row.SalesPhone,
            row.SalesEmail);
    }

    public async Task<CustomerDashboardResponse> GetDashboardAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var company = await GetCompanyAsync(userId, cancellationToken);
        var today = EquipmentAvailabilityService.BusinessToday();
        var endingSoonLimit = today.AddDays(EndingSoonDays);

        var contracts = await db.RentalContracts
            .AsNoTracking()
            .Where(c => c.CompanyId == company.Id &&
                        (ActiveStatuses.Contains(c.Status) || c.Status == RentalContractStatus.PendingSignature))
            .Select(c => new
            {
                c.Id,
                c.ContractCode,
                c.StartDate,
                c.EndDate,
                c.Status,
                c.ContractTotalAmount,
                c.DepositRequiredAmount,
                c.DepositPaidAmount,
            })
            .ToListAsync(cancellationToken);

        var active = contracts.Where(c => ActiveStatuses.Contains(c.Status)).ToList();
        var activeIds = active.Select(c => c.Id).ToList();

        var rentedUnits = await db.RentalContractItems
            .AsNoTracking()
            .Where(i => activeIds.Contains(i.RentalContractId))
            .SumAsync(i => (int?)i.Quantity, cancellationToken) ?? 0;

        var unpaidCharges = await (
            from charge in db.RentalCharges.AsNoTracking()
            join contract in db.RentalContracts.AsNoTracking() on charge.RentalContractId equals contract.Id
            where contract.CompanyId == company.Id &&
                  (charge.Status == RentalChargeStatus.Unpaid || charge.Status == RentalChargeStatus.PartiallyPaid)
            select (decimal?)(charge.Amount - charge.PaidAmount)
        ).SumAsync(cancellationToken) ?? 0;

        var unpaidDeposits = active.Sum(c => Math.Max(0m, c.DepositRequiredAmount - c.DepositPaidAmount));
        var outstanding = unpaidCharges + unpaidDeposits;

        var upcoming = contracts
            .OrderBy(c => c.EndDate)
            .Take(5)
            .Select(c => new CustomerContractBrief(
                c.Id,
                c.ContractCode,
                c.StartDate,
                c.EndDate,
                EnumName.ToDatabaseValue(c.Status),
                c.ContractTotalAmount,
                c.EndDate.DayNumber - today.DayNumber))
            .ToList();

        return new CustomerDashboardResponse(
            company,
            active.Count,
            contracts.Count(c => c.Status == RentalContractStatus.PendingSignature),
            active.Count(c => c.EndDate >= today && c.EndDate <= endingSoonLimit),
            rentedUnits,
            active.Sum(c => c.ContractTotalAmount),
            outstanding,
            upcoming);
    }
}
