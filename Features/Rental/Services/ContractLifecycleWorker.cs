using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>
/// Tiến trình chạy nền theo dõi vòng đời hợp đồng & công nợ:
/// 1) Nhắc sắp hết hạn hợp đồng trước 03 ngày để chuẩn bị lịch thu hồi.
/// 2) Tự động chuyển hợp đồng đang DELIVERED / PARTIALLY_DELIVERED sang EXPIRED khi đã quá ngày EndDate.
/// 3) Nhắc hạn thanh toán đợt tiền thuê (RENTAL) trước 03 ngày.
/// 4) Tự động tính & cập nhật phạt chậm thanh toán (LATE_PAYMENT = 0,05%/ngày trên số tiền quá hạn, trần tối đa 8%)
///    và cảnh báo đỏ tới Quản lý & Sales nếu chậm thanh toán từ 15 ngày trở lên.
/// </summary>
public sealed class ContractLifecycleWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ContractLifecycleWorker> logger) : BackgroundService
{
    public const decimal LatePaymentDailyRate = 0.0005m; // 0.05% / ngày
    public const decimal LatePaymentMaxCapRate = 0.08m;  // trần tối đa 8% theo Luật Thương mại VN
    private const int ReminderDaysBefore = 3;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lỗi trong ContractLifecycleWorker.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunCycleAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationDispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

        var today = EquipmentAvailabilityService.BusinessToday();
        var vnTodayStartUtc = EquipmentAvailabilityService.StartOfBusinessDay(today).ToUniversalTime();

        await ProcessEndingSoonContractsAsync(db, notificationDispatcher, today, vnTodayStartUtc, ct);
        await ProcessExpiredContractsAsync(db, notificationDispatcher, today, ct);
        await ProcessDueSoonRentalChargesAsync(db, notificationDispatcher, today, vnTodayStartUtc, ct);
        await ProcessOverdueRentalChargesAsync(db, notificationDispatcher, today, vnTodayStartUtc, ct);
    }

    private async Task ProcessEndingSoonContractsAsync(
        AppDbContext db,
        INotificationDispatcher notificationDispatcher,
        DateOnly today,
        DateTimeOffset vnTodayStartUtc,
        CancellationToken ct)
    {
        var reminderLimit = today.AddDays(ReminderDaysBefore);
        var contracts = await db.RentalContracts.AsNoTracking()
            .Where(c => (c.Status == RentalContractStatus.Delivered || c.Status == RentalContractStatus.PartiallyDelivered)
                        && c.EndDate >= today
                        && c.EndDate <= reminderLimit)
            .ToListAsync(ct);

        foreach (var c in contracts)
        {
            var markerTitle = $"Sắp hết hạn hợp đồng {c.ContractCode}";
            var alreadySentToday = await db.Notifications.AsNoTracking()
                .AnyAsync(n => n.Type == "CONTRACT_ENDING_SOON"
                               && n.Title == markerTitle
                               && n.CreatedAt >= vnTodayStartUtc, ct);
            if (alreadySentToday) continue;

            var daysLeft = c.EndDate.DayNumber - today.DayNumber;
            var dayText = daysLeft == 0 ? "hôm nay" : $"sau {daysLeft} ngày nữa";
            var msg = $"Hợp đồng {c.ContractCode} sẽ hết hạn vào ngày {c.EndDate:dd/MM/yyyy} ({dayText}). " +
                      $"Vui lòng chuẩn bị bàn giao/thu hồi thiết bị đúng hạn (quá hạn sẽ tính phí trả trễ 150% đơn giá ngày).";

            try
            {
                var repEmail = await db.RentalContractRepresentatives.AsNoTracking()
                    .Where(r => r.RentalContractId == c.Id && r.Email != null)
                    .Select(r => r.Email!)
                    .FirstOrDefaultAsync(ct);

                await notificationDispatcher.NotifyCompanyAsync(
                    c.CompanyId,
                    markerTitle,
                    msg,
                    "CONTRACT_ENDING_SOON",
                    extraEmails: repEmail is not null ? [repEmail] : null,
                    cancellationToken: ct);

                var staffIds = new List<Guid> { c.CreatedBy };
                if (c.ApprovedBy.HasValue) staffIds.Add(c.ApprovedBy.Value);

                await notificationDispatcher.NotifyUsersAsync(
                    staffIds.Distinct(),
                    markerTitle,
                    $"Hợp đồng {c.ContractCode} sắp hết hạn ngày {c.EndDate:dd/MM/yyyy} ({dayText}). Quản lý hãy lên lịch chuyến thu hồi thiết bị (RETRIEVAL).",
                    "CONTRACT_ENDING_SOON",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không gửi được thông báo sắp hết hạn cho {ContractCode}.", c.ContractCode);
            }
        }
    }

    private async Task ProcessExpiredContractsAsync(
        AppDbContext db,
        INotificationDispatcher notificationDispatcher,
        DateOnly today,
        CancellationToken ct)
    {
        var candidateIds = await db.RentalContracts.AsNoTracking()
            .Where(c => (c.Status == RentalContractStatus.Delivered || c.Status == RentalContractStatus.PartiallyDelivered)
                        && c.EndDate < today)
            .Select(c => c.Id)
            .ToListAsync(ct);

        foreach (var contractId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var c = await db.RentalContracts
                .FromSqlInterpolated($"SELECT * FROM rental_contracts WHERE id = {contractId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);

            if (c is null
                || c.Status is not (RentalContractStatus.Delivered or RentalContractStatus.PartiallyDelivered)
                || c.EndDate >= today)
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var prev = c.Status;
            c.Status = RentalContractStatus.Expired;
            c.UpdatedAt = now;

            db.ContractStatusHistory.Add(new ContractStatusHistory
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = c.Id,
                PreviousStatus = prev,
                Status = RentalContractStatus.Expired,
                ChangedBy = c.ApprovedBy ?? c.CreatedBy,
                ChangedAt = now,
                Note = $"Hợp đồng đã quá ngày kết thúc ({c.EndDate:dd/MM/yyyy}) nhưng vẫn còn thiết bị ngoài hiện trường — Tự động chuyển EXPIRED chờ thu hồi & quyết toán.",
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            try
            {
                var repEmail = await db.RentalContractRepresentatives.AsNoTracking()
                    .Where(r => r.RentalContractId == c.Id && r.Email != null)
                    .Select(r => r.Email!)
                    .FirstOrDefaultAsync(ct);

                var msg = $"Hợp đồng {c.ContractCode} đã hết hạn thuê từ ngày {c.EndDate:dd/MM/yyyy}. " +
                          $"Vui lòng phối hợp bàn giao lại thiết bị cho đội kỹ thuật thu hồi. Thời gian trả trễ sẽ tính phí 150% đơn giá ngày theo Điều 7.";

                await notificationDispatcher.NotifyCompanyAsync(
                    c.CompanyId,
                    $"Hợp đồng {c.ContractCode} đã hết hạn — Cần thu hồi thiết bị",
                    msg,
                    "CONTRACT_EXPIRED",
                    extraEmails: repEmail is not null ? [repEmail] : null,
                    cancellationToken: ct);

                var staffIds = new List<Guid> { c.CreatedBy };
                if (c.ApprovedBy.HasValue) staffIds.Add(c.ApprovedBy.Value);

                await notificationDispatcher.NotifyUsersAsync(
                    staffIds.Distinct(),
                    $"Hợp đồng {c.ContractCode} đã hết hạn (EXPIRED)",
                    $"Hợp đồng {c.ContractCode} đã quá ngày {c.EndDate:dd/MM/yyyy}. Vui lòng tạo chuyến thu hồi (RETRIEVAL) và theo dõi phí trả trễ.",
                    "CONTRACT_EXPIRED",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không gửi được thông báo EXPIRED cho {ContractCode}.", c.ContractCode);
            }
        }
    }

    private async Task ProcessDueSoonRentalChargesAsync(
        AppDbContext db,
        INotificationDispatcher notificationDispatcher,
        DateOnly today,
        DateTimeOffset vnTodayStartUtc,
        CancellationToken ct)
    {
        var reminderLimit = today.AddDays(ReminderDaysBefore);
        var rows = await (
            from rc in db.RentalCharges.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on rc.RentalContractId equals c.Id
            where rc.ChargeType == "RENTAL"
                  && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                  && rc.Amount > rc.PaidAmount
                  && rc.DueDate != null
                  && rc.DueDate >= today
                  && rc.DueDate <= reminderLimit
                  && c.Status != RentalContractStatus.Cancelled
                  && c.Status != RentalContractStatus.Draft
            select new { rc, c }
        ).ToListAsync(ct);

        foreach (var row in rows)
        {
            var title = $"Nhắc hạn thanh toán ({row.c.ContractCode})";
            var alreadySent = await db.Notifications.AsNoTracking()
                .AnyAsync(n => n.Type == "RENTAL_CHARGE_DUE_SOON"
                               && n.Title == title
                               && n.CreatedAt >= vnTodayStartUtc, ct);
            if (alreadySent) continue;

            var remaining = row.rc.Amount - row.rc.PaidAmount;
            var msg = $"Khoản \"{row.rc.Description}\" của hợp đồng {row.c.ContractCode} còn thiếu {remaining:N0} đ, " +
                      $"hạn thanh toán ngày {row.rc.DueDate:dd/MM/yyyy}. Sau ngày này sẽ áp dụng phạt chậm thanh toán 0,05%/ngày (tối đa 8%).";

            try
            {
                await notificationDispatcher.NotifyCompanyAsync(
                    row.c.CompanyId,
                    title,
                    msg,
                    "RENTAL_CHARGE_DUE_SOON",
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không gửi được thông báo nhắc hạn thanh toán cho {ContractCode}.", row.c.ContractCode);
            }
        }
    }

    private async Task ProcessOverdueRentalChargesAsync(
        AppDbContext db,
        INotificationDispatcher notificationDispatcher,
        DateOnly today,
        DateTimeOffset vnTodayStartUtc,
        CancellationToken ct)
    {
        var overdueRows = await (
            from rc in db.RentalCharges.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on rc.RentalContractId equals c.Id
            where rc.ChargeType == "RENTAL"
                  && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                  && rc.Amount > rc.PaidAmount
                  && rc.DueDate != null
                  && rc.DueDate < today
                  && c.Status != RentalContractStatus.Cancelled
                  && c.Status != RentalContractStatus.Draft
                  && c.Status != RentalContractStatus.PendingApproval
                  && c.Status != RentalContractStatus.Rejected
                  && c.Status != RentalContractStatus.PendingSignature
            select new { rc, c }
        ).ToListAsync(ct);

        if (overdueRows.Count == 0) return;

        var now = DateTimeOffset.UtcNow;

        foreach (var row in overdueRows)
        {
            var overdueDays = today.DayNumber - row.rc.DueDate!.Value.DayNumber;
            if (overdueDays <= 0) continue;

            var overduePrincipal = row.rc.Amount - row.rc.PaidAmount;
            var rawPenalty = Math.Round(overduePrincipal * LatePaymentDailyRate * overdueDays, 0, MidpointRounding.AwayFromZero);
            var maxCap = Math.Round(row.rc.Amount * LatePaymentMaxCapRate, 0, MidpointRounding.AwayFromZero);
            var penaltyAmount = Math.Min(rawPenalty, maxCap);
            if (penaltyAmount <= 0) continue;

            var marker = $"[REF:{row.rc.Id}]";
            var existingPenalty = await db.RentalCharges
                .FirstOrDefaultAsync(p => p.RentalContractId == row.c.Id
                                          && p.ChargeType == "LATE_PAYMENT"
                                          && p.Note != null
                                          && p.Note.Contains(marker), ct);

            var desc = $"Phạt chậm thanh toán ({overdueDays} ngày × 0,05%, trần 8%) — {row.rc.Description}";
            var note = $"{marker} Quá hạn từ {row.rc.DueDate:dd/MM/yyyy} trên số dư {overduePrincipal:N0} đ.";

            if (existingPenalty is null)
            {
                db.RentalCharges.Add(new RentalCharge
                {
                    Id = Guid.CreateVersion7(),
                    SourceType = "CONTRACT",
                    RentalContractId = row.c.Id,
                    ChargeType = "LATE_PAYMENT",
                    Description = desc,
                    Quantity = overdueDays,
                    UnitPrice = Math.Round(overduePrincipal * LatePaymentDailyRate, 2),
                    Amount = penaltyAmount,
                    DueDate = today,
                    PaidAmount = 0m,
                    Status = RentalChargeStatus.Unpaid,
                    CreatedBy = row.c.ApprovedBy ?? row.c.CreatedBy,
                    CreatedAt = now,
                    Note = note,
                });
                await db.SaveChangesAsync(ct);
            }
            else if (existingPenalty.Status is RentalChargeStatus.Unpaid or RentalChargeStatus.PartiallyPaid)
            {
                var nextAmount = Math.Max(existingPenalty.PaidAmount, penaltyAmount);
                if (existingPenalty.Amount != nextAmount || existingPenalty.Quantity != overdueDays)
                {
                    existingPenalty.Quantity = overdueDays;
                    existingPenalty.UnitPrice = Math.Round(overduePrincipal * LatePaymentDailyRate, 2);
                    existingPenalty.Amount = nextAmount;
                    existingPenalty.Description = desc;
                    existingPenalty.Note = note;
                    await db.SaveChangesAsync(ct);
                }
            }

            if (overdueDays is 1 or 7 or 15)
            {
                var alertTitle = overdueDays >= 15
                    ? $"[KHẨN] Chậm thanh toán quá 15 ngày ({row.c.ContractCode})"
                    : $"Quá hạn thanh toán {overdueDays} ngày ({row.c.ContractCode})";

                var alreadyAlerted = await db.Notifications.AsNoTracking()
                    .AnyAsync(n => n.Type == "LATE_PAYMENT_ALERT"
                                   && n.Title == alertTitle
                                   && n.CreatedAt >= vnTodayStartUtc, ct);
                if (alreadyAlerted) continue;

                try
                {
                    await notificationDispatcher.NotifyCompanyAsync(
                        row.c.CompanyId,
                        alertTitle,
                        $"Khoản \"{row.rc.Description}\" ({row.c.ContractCode}) đã quá hạn {overdueDays} ngày (còn thiếu {overduePrincipal:N0} đ, phí chậm thanh toán tạm tính {penaltyAmount:N0} đ). Vui lòng thanh toán ngay trên ứng dụng.",
                        "LATE_PAYMENT_ALERT",
                        cancellationToken: ct);

                    var staffIds = new List<Guid> { row.c.CreatedBy };
                    if (row.c.ApprovedBy.HasValue) staffIds.Add(row.c.ApprovedBy.Value);

                    var staffMsg = overdueDays >= 15
                        ? $"Khách hàng hợp đồng {row.c.ContractCode} đã chậm thanh toán {overdueDays} ngày (thiếu {overduePrincipal:N0} đ + phạt {penaltyAmount:N0} đ). Đã đủ điều kiện đơn phương chấm dứt & thu hồi thiết bị theo Điều 8!"
                        : $"Hợp đồng {row.c.ContractCode} quá hạn thanh toán {overdueDays} ngày (thiếu {overduePrincipal:N0} đ, phạt {penaltyAmount:N0} đ).";

                    await notificationDispatcher.NotifyUsersAsync(
                        staffIds.Distinct(),
                        alertTitle,
                        staffMsg,
                        "LATE_PAYMENT_ALERT",
                        cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Không gửi được cảnh báo quá hạn cho {ContractCode}.", row.c.ContractCode);
                }
            }
        }
    }
}
