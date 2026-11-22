using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Services;

/// <summary>
/// Kết quả tính số lượng khả dụng của một model trong một khoảng ngày.
/// </summary>
public sealed record EquipmentAvailabilityResult(
    Guid EquipmentModelId,
    int OperationalUnits,
    int PeakHeldQuantity,
    int AvailableQuantity
);

/// <summary>
/// Công thức chuẩn tính số lượng thiết bị khả dụng theo model.
///
/// Khả dụng [from, to] = Số máy hoạt động − Mức chiếm dụng cao nhất trong 1 ngày của [from, to]
///
/// - Số máy hoạt động: equipments của model có trạng thái AVAILABLE, RESERVED hoặc RENTED.
///   (Không tính MAINTENANCE, DAMAGED, LOST, DISPOSED.)
/// - Chiếm dụng gồm:
///   + Phiếu giữ chỗ PENDING/APPROVED chưa quá expires_at, có ngày giao với [from, to].
///   + Hợp đồng chưa kết thúc (mọi trạng thái trừ COMPLETED, EXPIRED, CANCELLED), có ngày giao
///     với [from, to]. Hợp đồng DELIVERED/PARTIALLY_DELIVERED quá end_date mà chưa kết thúc
///     được coi là vẫn chiếm máy đến hôm nay.
/// - Mức chiếm dụng lấy theo ngày cao nhất (không cộng dồn các khoảng không trùng nhau).
///
/// Mọi nghiệp vụ cần số lượng khả dụng (đặt thuê, hợp đồng, kho...) phải gọi service này,
/// không tự viết lại công thức. Xem thêm Docs/CongThucSoLuongKhaDung.md.
/// </summary>
public sealed class EquipmentAvailabilityService(AppDbContext db)
{
    // Ngày nghiệp vụ tính theo giờ Việt Nam (UTC+7).
    private static readonly TimeSpan BusinessUtcOffset = TimeSpan.FromHours(7);

    private sealed record Hold(Guid EquipmentModelId, DateOnly Start, DateOnly End, int Quantity);

    public static DateOnly BusinessToday()
        => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(BusinessUtcOffset).DateTime);

    public async Task<EquipmentAvailabilityResult> GetAvailabilityAsync(
        Guid equipmentModelId,
        DateOnly from,
        DateOnly to,
        Guid? excludeReservationId = null,
        Guid? excludeRentalContractId = null,
        CancellationToken cancellationToken = default)
    {
        var results = await GetAvailabilityAsync(
            [equipmentModelId],
            from,
            to,
            excludeReservationId,
            excludeRentalContractId,
            cancellationToken);

        return results[equipmentModelId];
    }

    /// <param name="excludeReservationId">
    /// Bỏ qua phiếu đang chỉnh sửa để không tự trừ số lượng của chính nó.
    /// </param>
    /// <param name="excludeRentalContractId">
    /// Bỏ qua hợp đồng đang chỉnh sửa để không tự trừ số lượng của chính nó.
    /// </param>
    public async Task<IReadOnlyDictionary<Guid, EquipmentAvailabilityResult>> GetAvailabilityAsync(
        IReadOnlyCollection<Guid> equipmentModelIds,
        DateOnly from,
        DateOnly to,
        Guid? excludeReservationId = null,
        Guid? excludeRentalContractId = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new AppException(
                "INVALID_DATE_RANGE",
                "Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu.",
                StatusCodes.Status400BadRequest,
                "to");
        }

        var ids = equipmentModelIds.Distinct().ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, EquipmentAvailabilityResult>();

        var now = DateTimeOffset.UtcNow;
        var today = BusinessToday();

        // "from" là từ khóa trong cú pháp truy vấn LINQ nên dùng tên khác.
        var rangeStart = from;
        var rangeEnd = to;

        // 1. Số máy hoạt động theo model.
        var operationalUnits = await db.Equipments
            .AsNoTracking()
            .Where(e => ids.Contains(e.EquipmentModelId) &&
                        (e.Status == EquipmentStatus.Available ||
                         e.Status == EquipmentStatus.Reserved ||
                         e.Status == EquipmentStatus.Rented))
            .GroupBy(e => e.EquipmentModelId)
            .Select(g => new { EquipmentModelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EquipmentModelId, x => x.Count, cancellationToken);

        // 2. Chiếm dụng bởi phiếu giữ chỗ còn hiệu lực.
        var reservationHolds = await (
            from item in db.ReservationItems.AsNoTracking()
            join reservation in db.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where ids.Contains(item.EquipmentModelId)
                  && (reservation.Status == ReservationStatus.Pending ||
                      reservation.Status == ReservationStatus.Approved)
                  && reservation.ExpiresAt > now
                  && reservation.RequestedFrom <= rangeEnd
                  && reservation.RequestedTo >= rangeStart
                  && (excludeReservationId == null || reservation.Id != excludeReservationId)
            select new Hold(
                item.EquipmentModelId,
                reservation.RequestedFrom,
                reservation.RequestedTo,
                item.Quantity)
        ).ToListAsync(cancellationToken);

        // 3. Chiếm dụng bởi hợp đồng chưa kết thúc.
        var contractRows = await (
            from item in db.RentalContractItems.AsNoTracking()
            join contract in db.RentalContracts.AsNoTracking()
                on item.RentalContractId equals contract.Id
            where ids.Contains(item.EquipmentModelId)
                  && contract.Status != RentalContractStatus.Completed
                  && contract.Status != RentalContractStatus.Expired
                  && contract.Status != RentalContractStatus.Cancelled
                  && contract.StartDate <= rangeEnd
                  && (contract.EndDate >= rangeStart ||
                      ((contract.Status == RentalContractStatus.Delivered ||
                        contract.Status == RentalContractStatus.PartiallyDelivered) &&
                       today >= rangeStart))
                  && (excludeRentalContractId == null || contract.Id != excludeRentalContractId)
            select new
            {
                item.EquipmentModelId,
                contract.StartDate,
                contract.EndDate,
                contract.Status,
                item.Quantity
            }
        ).ToListAsync(cancellationToken);

        var contractHolds = contractRows.Select(row =>
        {
            var isOutOnSite = row.Status is RentalContractStatus.Delivered
                or RentalContractStatus.PartiallyDelivered;

            // Đã giao, quá hạn mà chưa kết thúc: máy vẫn đang ở chỗ khách.
            var effectiveEnd = isOutOnSite && row.EndDate < today ? today : row.EndDate;

            return new Hold(row.EquipmentModelId, row.StartDate, effectiveEnd, row.Quantity);
        });

        var holdsByModel = reservationHolds
            .Concat(contractHolds)
            .ToLookup(h => h.EquipmentModelId);

        return ids.ToDictionary(
            id => id,
            id =>
            {
                var units = operationalUnits.GetValueOrDefault(id);
                var peak = CalculatePeakQuantity(holdsByModel[id], from, to);

                return new EquipmentAvailabilityResult(
                    id,
                    units,
                    peak,
                    Math.Max(0, units - peak));
            });
    }

    /// <summary>
    /// Mức chiếm dụng cao nhất trong một ngày thuộc [from, to] (thuật toán quét sự kiện).
    /// </summary>
    private static int CalculatePeakQuantity(IEnumerable<Hold> holds, DateOnly from, DateOnly to)
    {
        // Ngày bắt đầu: +quantity; ngày sau ngày kết thúc: −quantity.
        var changes = new SortedDictionary<DateOnly, int>();

        foreach (var hold in holds)
        {
            var start = hold.Start < from ? from : hold.Start;
            var end = hold.End > to ? to : hold.End;

            if (start > end)
                continue;

            var dayAfterEnd = end.AddDays(1);

            changes[start] = changes.GetValueOrDefault(start) + hold.Quantity;
            changes[dayAfterEnd] = changes.GetValueOrDefault(dayAfterEnd) - hold.Quantity;
        }

        var current = 0;
        var peak = 0;

        foreach (var change in changes.Values)
        {
            current += change;
            peak = Math.Max(peak, current);
        }

        return peak;
    }
}
