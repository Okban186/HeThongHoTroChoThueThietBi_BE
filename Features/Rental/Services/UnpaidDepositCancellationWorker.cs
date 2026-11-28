using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>
/// Tiến trình chạy nền tự động hủy hợp đồng quá hạn đặt cọc:
/// Tính lùi 02 ngày trước ngày bắt đầu thuê (VD: thuê ngày 29 → tới ngày 27 mà chưa nhận đủ tiền cọc
/// thì tự động chuyển hợp đồng sang CANCELLED, hủy các giao dịch/khoản thu đang chờ và nhả thiết bị).
/// </summary>
public sealed class UnpaidDepositCancellationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<UnpaidDepositCancellationWorker> logger) : BackgroundService
{
    private const int DeadlineDaysBeforeStart = 2;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private static readonly RentalContractStatus[] AutoCancellableStatuses =
    [
        RentalContractStatus.PendingSignature,
        RentalContractStatus.Signed,
        RentalContractStatus.PendingDeposit,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CancelOverdueDepositContractsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lỗi khi chạy tiến trình tự động hủy hợp đồng quá hạn đặt cọc.");
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

    public async Task<int> CancelOverdueDepositContractsAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationDispatcher = scope.ServiceProvider.GetRequiredService<INotificationDispatcher>();

        var today = EquipmentAvailabilityService.BusinessToday();
        var cutoffStartDate = today.AddDays(DeadlineDaysBeforeStart);

        var candidateIds = await db.RentalContracts.AsNoTracking()
            .Where(c => AutoCancellableStatuses.Contains(c.Status)
                        && c.DepositRequiredAmount > 0
                        && c.DepositPaidAmount < c.DepositRequiredAmount
                        && c.StartDate <= cutoffStartDate)
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
            return 0;

        var cancelledCount = 0;

        foreach (var contractId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var contract = await db.RentalContracts
                .FromSqlInterpolated($"SELECT * FROM rental_contracts WHERE id = {contractId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);

            if (contract is null
                || !AutoCancellableStatuses.Contains(contract.Status)
                || contract.DepositRequiredAmount <= 0
                || contract.DepositPaidAmount >= contract.DepositRequiredAmount
                || contract.StartDate > cutoffStartDate)
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var previousStatus = contract.Status;
            var deadlineDate = contract.StartDate.AddDays(-DeadlineDaysBeforeStart);
            var reason = $"Tự động hủy hợp đồng do chưa hoàn tất tiền đặt cọc ({contract.DepositPaidAmount:N0}/{contract.DepositRequiredAmount:N0} đ) " +
                         $"trước ngày {deadlineDate:dd/MM/yyyy} (02 ngày trước ngày bắt đầu thuê {contract.StartDate:dd/MM/yyyy}).";

            contract.Status = RentalContractStatus.Cancelled;
            contract.UpdatedAt = now;

            db.ContractStatusHistory.Add(new ContractStatusHistory
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                PreviousStatus = previousStatus,
                Status = RentalContractStatus.Cancelled,
                ChangedBy = contract.ApprovedBy ?? contract.CreatedBy,
                ChangedAt = now,
                Note = reason,
            });

            var pendingTxns = await db.PaymentTransactions
                .Where(t => t.RentalContractId == contract.Id && t.Status == PaymentTransactionStatus.Pending)
                .ToListAsync(ct);

            foreach (var p in pendingTxns)
            {
                p.Status = PaymentTransactionStatus.Cancelled;
                p.RejectedReason = "Hợp đồng đã tự động hủy do quá hạn đặt cọc.";
                p.UpdatedAt = now;
            }

            var unpaidCharges = await db.RentalCharges
                .Where(rc => rc.RentalContractId == contract.Id && rc.Status == RentalChargeStatus.Unpaid)
                .ToListAsync(ct);

            foreach (var ch in unpaidCharges)
            {
                ch.Status = RentalChargeStatus.Cancelled;
                ch.Note = string.IsNullOrWhiteSpace(ch.Note)
                    ? "Tự hủy theo hợp đồng quá hạn đặt cọc."
                    : $"{ch.Note} | Tự hủy theo hợp đồng quá hạn đặt cọc.";
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            cancelledCount++;

            logger.LogInformation(
                "Đã tự động hủy hợp đồng {ContractCode} ({ContractId}) do quá hạn đặt cọc trước ngày thuê {StartDate}.",
                contract.ContractCode, contract.Id, contract.StartDate);

            try
            {
                var repEmail = await db.RentalContractRepresentatives.AsNoTracking()
                    .Where(r => r.RentalContractId == contract.Id && r.Email != null)
                    .Select(r => r.Email!)
                    .FirstOrDefaultAsync(ct);

                await notificationDispatcher.NotifyCompanyAsync(
                    contract.CompanyId,
                    $"Hợp đồng {contract.ContractCode} đã tự động hủy do quá hạn đặt cọc",
                    reason,
                    "CONTRACT_AUTO_CANCELLED_DEPOSIT",
                    extraEmails: repEmail is not null ? [repEmail] : null,
                    cancellationToken: ct);

                var staffRecipients = new List<Guid> { contract.CreatedBy };
                if (contract.ApprovedBy.HasValue) staffRecipients.Add(contract.ApprovedBy.Value);

                await notificationDispatcher.NotifyUsersAsync(
                    staffRecipients.Distinct(),
                    $"Tự động hủy hợp đồng {contract.ContractCode} (Quá hạn đặt cọc)",
                    reason,
                    "CONTRACT_AUTO_CANCELLED_DEPOSIT",
                    cancellationToken: ct);
            }
            catch (Exception notifyEx)
            {
                logger.LogWarning(notifyEx, "Không gửi được thông báo tự động hủy hợp đồng {ContractCode}.", contract.ContractCode);
            }
        }

        return cancelledCount;
    }
}
