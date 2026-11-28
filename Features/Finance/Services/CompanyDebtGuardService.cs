using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Services;

/// <summary>
/// Kiểm tra công nợ chưa thanh toán của Công ty trước khi cho phép tạo phiếu giữ chỗ hoặc hợp đồng mới:
/// - Chặn nếu công ty còn bất kỳ khoản phạt/bồi thường/phí vận chuyển chưa trả hết.
/// - Chặn nếu có đợt tiền thuê (RENTAL) đã đến/quá hạn thanh toán (DueDate <= hôm nay) hoặc thuộc hợp đồng đã kết thúc/hết hạn mà chưa trả đủ.
/// - Chặn nếu còn khoản phí sự cố (incident_charges) đã duyệt (PENDING) chưa thanh toán xong.
/// - Không chặn các đợt thuê dài hạn trong tương lai chưa đến hạn thanh toán.
/// </summary>
public sealed class CompanyDebtGuardService(AppDbContext db)
{
    public async Task EnsureCompanyHasNoBlockingDebtAsync(
        Guid companyId,
        Guid? excludeContractId = null,
        CancellationToken cancellationToken = default)
    {
        var today = EquipmentAvailabilityService.BusinessToday();

        var unpaidRentalCharges = await (
            from rc in db.RentalCharges.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on rc.RentalContractId equals c.Id
            where c.CompanyId == companyId
                  && c.Status != RentalContractStatus.Cancelled
                  && (!excludeContractId.HasValue || c.Id != excludeContractId.Value)
                  && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                  && rc.Amount > rc.PaidAmount
                  && (
                      rc.ChargeType != "RENTAL"
                      || (rc.DueDate != null && rc.DueDate <= today)
                      || c.Status == RentalContractStatus.Completed
                      || c.Status == RentalContractStatus.Expired
                  )
            select new
            {
                c.ContractCode,
                rc.Id,
                Remaining = rc.Amount - rc.PaidAmount
            }
        ).ToListAsync(cancellationToken);

        var linkedRentalChargeIds = unpaidRentalCharges.Select(x => x.Id).ToHashSet();

        // Các incident_charges đang PENDING (đã được Manager duyệt) chưa gắn vào rental_charge đã đếm ở trên
        var unpaidIncidentCharges = await (
            from ic in db.IncidentCharges.AsNoTracking()
            join ii in db.EquipmentIncidentItems.AsNoTracking() on ic.IncidentItemId equals ii.Id
            join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
            where inc.CompanyId == companyId
                  && ic.Status == IncidentChargeStatus.Pending
                  && ic.Amount > 0
                  && (ic.RentalChargeId == null || !linkedRentalChargeIds.Contains(ic.RentalChargeId.Value))
            select new
            {
                inc.IncidentCode,
                Remaining = Math.Max(0m, ic.Amount - ic.DepositOffsetAmount)
            }
        ).ToListAsync(cancellationToken);

        var totalDebt = unpaidRentalCharges.Sum(x => x.Remaining)
                      + unpaidIncidentCharges.Sum(x => x.Remaining);

        if (totalDebt <= 0)
            return;

        var refs = unpaidRentalCharges.Select(x => x.ContractCode)
            .Concat(unpaidIncidentCharges.Select(x => x.IncidentCode))
            .Distinct()
            .Take(4)
            .ToList();

        var refText = refs.Count > 0 ? $" ({string.Join(", ", refs)})" : "";

        throw new AppException(
            "COMPANY_HAS_UNPAID_DEBT",
            $"Khách hàng đang còn khoản phí/công nợ chưa thanh toán tổng cộng {totalDebt:N0} đ{refText}. Vui lòng thanh toán hết công nợ cũ trước khi lập phiếu giữ chỗ hoặc hợp đồng mới.",
            StatusCodes.Status409Conflict);
    }
}
