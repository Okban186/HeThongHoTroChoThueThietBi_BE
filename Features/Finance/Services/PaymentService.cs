using System.Globalization;
using System.Security.Cryptography;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Customer.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Configurations;
using HeThongHoTroChoThueThietBi.Features.Finance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Services;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Services;

/// <summary>
/// Quản lý thanh toán toàn bộ khoản phải thu của hợp đồng:
/// - Tiền đặt cọc (DEPOSIT)
/// - Các đợt tiền thuê (RENTAL_INSTALLMENT)
/// - Các khoản phạt &amp; phí phát sinh (CHARGE từ rental_charges)
/// - Phí sự cố (INCIDENT_CHARGE từ incident_charges)
///
/// Hỗ trợ 2 nhánh:
/// 1. VNPAY Sandbox (tạo link thanh toán -> Flutter vẽ QR, hạn 15 phút, IPN/Return gạch nợ tự động &amp; idempotent).
/// 2. Tiền mặt (CASH: chụp ảnh phiếu thu/biên bản -> PENDING -> Manager duyệt mới VERIFIED &amp; gạch nợ).
/// </summary>
public sealed class PaymentService(
    AppDbContext db,
    IOptions<VnpayOptions> vnpayOptions,
    IFileUploadService uploadService,
    CustomerPortalService customerPortalService,
    INotificationDispatcher notificationDispatcher,
    ILogger<PaymentService> logger)
{
    private readonly VnpayOptions _vnpay = vnpayOptions.Value;

    private static readonly FileUploadPolicy CashReceiptUploadPolicy = new()
    {
        MaxFileCount = 1,
        MaxFileSizeBytes = 5 * 1024 * 1024,
        AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
        ResourceType = CloudinaryResourceType.Image,
        Folder = StorageFolder.Contract,
    };

    private static readonly string[] CashReceiptFormats = ["jpg", "jpeg", "png", "webp"];

    private static readonly RentalContractStatus[] PayableContractStatuses =
    [
        RentalContractStatus.PendingDeposit,
        RentalContractStatus.Preparing,
        RentalContractStatus.PartiallyDelivered,
        RentalContractStatus.Delivered,
        RentalContractStatus.Completed,
        RentalContractStatus.Expired,
        RentalContractStatus.CancellationRequested,
    ];

    // ================================================================ tổng quan tài chính hợp đồng

    /// <summary>
    /// Đảm bảo mọi hợp đồng đã ký (Signed trở đi) đều có sẵn các dòng tiền thuê (charge_type = 'RENTAL') trong rental_charges:
    /// - SHORT_TERM: 1 khoản duy nhất (100% tiền thuê hợp đồng + phí vận chuyển nếu có).
    /// - LONG_TERM: chia theo chu kỳ 30 ngày/đợt (đợt 1 gộp phí vận chuyển nếu có).
    /// Khắc phục trường hợp hợp đồng cũ hoặc được tạo trước khi bật tính năng thanh toán chỉ hiện tiền cọc.
    /// </summary>
    public async Task EnsureRentalChargesGeneratedAsync(
        RentalContract contract,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!PayableContractStatuses.Contains(contract.Status))
            return;

        var hasRentalCharge = await db.RentalCharges.AsNoTracking()
            .AnyAsync(c => c.RentalContractId == contract.Id
                           && c.ChargeType == "RENTAL"
                           && c.Status != RentalChargeStatus.Cancelled, cancellationToken);
        if (hasRentalCharge)
            return;

        var days = RentalPricing.RentalDays(contract.StartDate, contract.EndDate);
        var items = await db.RentalContractItems.AsNoTracking()
            .Where(i => i.RentalContractId == contract.Id)
            .ToListAsync(cancellationToken);

        var shippingFee = await db.RentalContractServices.AsNoTracking()
            .Where(s => s.RentalContractId == contract.Id && s.ProvidedBy == "RENTAL_COMPANY")
            .SumAsync(s => (decimal?)s.Amount, cancellationToken) ?? 0m;

        var itemsTotal = items.Sum(i => i.Quantity * i.AgreedUnitPrice * RentalPricing.BillableUnits(i.BillingUnit, days));
        var now = DateTimeOffset.UtcNow;
        var creatorId = contract.CreatedBy != Guid.Empty ? contract.CreatedBy : actorUserId;

        if (string.Equals(contract.RentalType, "SHORT_TERM", StringComparison.OrdinalIgnoreCase))
        {
            var amount = itemsTotal + shippingFee;
            db.RentalCharges.Add(new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "CONTRACT",
                RentalContractId = contract.Id,
                ChargeType = "RENTAL",
                Description = shippingFee > 0
                    ? $"Thanh toán hợp đồng ngắn hạn ({days} ngày) + Phí vận chuyển"
                    : $"Thanh toán hợp đồng ngắn hạn ({days} ngày)",
                Quantity = 1,
                UnitPrice = amount,
                Amount = amount,
                PeriodStart = contract.StartDate,
                PeriodEnd = contract.EndDate,
                DueDate = contract.EndDate.AddDays(7),
                PaidAmount = 0,
                Status = RentalChargeStatus.Unpaid,
                CreatedBy = creatorId,
                CreatedAt = now,
                Note = shippingFee > 0 ? $"Đã gộp phí vận chuyển {shippingFee:N0} đ" : null,
            });
        }
        else
        {
            var totalMonths = Math.Max(1, RentalPricing.BillableUnits("MONTHLY", days));
            var baseMonthlyAmount = Math.Floor(itemsTotal / totalMonths);

            for (var i = 0; i < totalMonths; i++)
            {
                var installmentNo = i + 1;
                var periodStart = contract.StartDate.AddDays(i * 30);
                var periodEnd = installmentNo == totalMonths
                    ? contract.EndDate
                    : contract.StartDate.AddDays(installmentNo * 30 - 1);

                var rentPart = installmentNo == totalMonths
                    ? itemsTotal - baseMonthlyAmount * (totalMonths - 1)
                    : baseMonthlyAmount;

                var shippingPart = installmentNo == 1 ? shippingFee : 0m;
                var amount = rentPart + shippingPart;
                var dueDate = periodStart.AddDays(5);

                db.RentalCharges.Add(new RentalCharge
                {
                    Id = Guid.CreateVersion7(),
                    SourceType = "CONTRACT",
                    RentalContractId = contract.Id,
                    ChargeType = "RENTAL",
                    Description = shippingPart > 0
                        ? $"Tiền thuê đợt {installmentNo}/{totalMonths} (gồm phí vận chuyển)"
                        : $"Tiền thuê đợt {installmentNo}/{totalMonths}",
                    Quantity = 1,
                    UnitPrice = amount,
                    Amount = amount,
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    DueDate = dueDate,
                    PaidAmount = 0,
                    Status = RentalChargeStatus.Unpaid,
                    CreatedBy = creatorId,
                    CreatedAt = now,
                    Note = shippingPart > 0 ? $"Gồm tiền thuê {rentPart:N0} đ + phí vận chuyển {shippingPart:N0} đ" : null,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ContractBillingSummaryResponse> GetContractBillingAsync(
        Guid contractId,
        Guid currentUserId,
        bool isCustomer,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        var contract = await EnsureContractAccessAsync(contractId, currentUserId, isCustomer, canViewAll, cancellationToken);
        await EnsureRentalChargesGeneratedAsync(contract, currentUserId, cancellationToken);
        await ExpireTimedOutTransactionsAsync(contractId, cancellationToken);

        var days = RentalPricing.RentalDays(contract.StartDate, contract.EndDate);

        var items = await db.RentalContractItems
            .AsNoTracking()
            .Where(i => i.RentalContractId == contractId)
            .ToListAsync(cancellationToken);

        var equipmentRentalTotal = items.Sum(i =>
            i.Quantity * i.AgreedUnitPrice * RentalPricing.BillableUnits(i.BillingUnit, days));

        var servicesTotal = await db.RentalContractServices
            .AsNoTracking()
            .Where(s => s.RentalContractId == contractId && s.ProvidedBy == "RENTAL_COMPANY")
            .SumAsync(s => (decimal?)s.Amount, cancellationToken) ?? 0m;

        var charges = await db.RentalCharges
            .AsNoTracking()
            .Where(c => c.RentalContractId == contractId && c.Status != RentalChargeStatus.Cancelled)
            .OrderBy(c => c.PeriodStart)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        var transactions = await db.PaymentTransactions
            .AsNoTracking()
            .Where(t => t.RentalContractId == contractId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var pendingCashTransactions = transactions
            .Where(t => t.PaymentMethod == PaymentMethod.Cash && t.Status == PaymentTransactionStatus.Pending)
            .ToList();

        // --- Các đợt tiền thuê (charge_type == RENTAL)
        var rentalCharges = charges.Where(c => c.ChargeType == "RENTAL").ToList();
        var totalInstallments = rentalCharges.Count;

        var installments = rentalCharges.Select((c, idx) =>
        {
            var remaining = Math.Max(0m, c.Amount - c.PaidAmount);
            return new ContractInstallmentDto(
                c.Id,
                idx + 1,
                totalInstallments,
                string.IsNullOrWhiteSpace(c.Description)
                    ? $"Tiền thuê đợt {idx + 1}/{totalInstallments}"
                    : c.Description,
                c.PeriodStart,
                c.PeriodEnd,
                c.DueDate,
                c.Amount,
                c.PaidAmount,
                remaining,
                EnumName.ToDatabaseValue(c.Status),
                c.PaidAt,
                c.Note,
                pendingCashTransactions.Any(t => t.RentalChargeId == c.Id));
        }).ToList();

        // --- Các khoản phạt / bồi thường từ rental_charges (khác RENTAL)
        var penaltyRentalCharges = charges.Where(c => c.ChargeType != "RENTAL").ToList();
        var penaltyRentalChargeIds = penaltyRentalCharges.Select(c => c.Id).ToHashSet();

        // Map thiết bị (serial) cho rental_charges nếu có
        var equipmentIdsFromCharges = penaltyRentalCharges
            .Where(c => c.EquipmentId.HasValue)
            .Select(c => c.EquipmentId!.Value)
            .Distinct()
            .ToList();

        var serialByEquipment = equipmentIdsFromCharges.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Equipments.AsNoTracking()
                .Where(e => equipmentIdsFromCharges.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.SerialNumber, cancellationToken);

        // Lấy incident_charges liên quan tới hợp đồng (qua rental_charge_id hoặc qua máy đã giao thuộc hợp đồng).
        var contractEquipmentIds = await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            where note.RentalContractId == contractId
            select item.EquipmentId
        ).Distinct().ToListAsync(cancellationToken);

        var incidentChargeRows = await (
            from ic in db.IncidentCharges.AsNoTracking()
            join ii in db.EquipmentIncidentItems.AsNoTracking() on ic.IncidentItemId equals ii.Id
            join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
            join eq in db.Equipments.AsNoTracking() on ii.EquipmentId equals eq.Id
            where ic.Status != IncidentChargeStatus.Cancelled &&
                  ic.Status != IncidentChargeStatus.PendingApproval &&
                  ((ic.RentalChargeId.HasValue && penaltyRentalChargeIds.Contains(ic.RentalChargeId.Value)) ||
                   (inc.CompanyId == contract.CompanyId && contractEquipmentIds.Contains(ii.EquipmentId)))
            orderby ic.CreatedAt descending
            select new
            {
                ic,
                inc.IncidentCode,
                eq.SerialNumber,
            }
        ).ToListAsync(cancellationToken);

        var incidentByRentalChargeId = incidentChargeRows
            .Where(x => x.ic.RentalChargeId.HasValue)
            .GroupBy(x => x.ic.RentalChargeId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var penalties = new List<ContractPenaltyDto>();

        foreach (var rc in penaltyRentalCharges)
        {
            var linkedIncident = incidentByRentalChargeId.GetValueOrDefault(rc.Id);
            var remaining = Math.Max(0m, rc.Amount - rc.PaidAmount);
            var isOffsetByDeposit =
                contract.DepositAppliedAmount > 0 &&
                !string.IsNullOrWhiteSpace(rc.Note) &&
                rc.Note.Contains("cấn trừ", StringComparison.OrdinalIgnoreCase);

            penalties.Add(new ContractPenaltyDto(
                rc.Id,
                "RENTAL_CHARGE",
                rc.Id,
                linkedIncident?.ic.Id,
                rc.ChargeType,
                string.IsNullOrWhiteSpace(rc.Description) ? ChargeTypeFallbackLabel(rc.ChargeType) : rc.Description,
                linkedIncident?.IncidentCode,
                linkedIncident?.SerialNumber ?? (rc.EquipmentId.HasValue ? serialByEquipment.GetValueOrDefault(rc.EquipmentId.Value) : null),
                rc.Amount,
                rc.PaidAmount,
                remaining,
                EnumName.ToDatabaseValue(rc.Status),
                isOffsetByDeposit,
                rc.CreatedAt,
                rc.PaidAt,
                rc.Note,
                pendingCashTransactions.Any(t => t.RentalChargeId == rc.Id)));
        }

        // Thêm các incident_charges chưa gắn với rental_charge nào của hợp đồng.
        foreach (var row in incidentChargeRows.Where(x => !x.ic.RentalChargeId.HasValue || !penaltyRentalChargeIds.Contains(x.ic.RentalChargeId.Value)))
        {
            var isSettled = row.ic.Status is IncidentChargeStatus.Waived or IncidentChargeStatus.Charged;
            var paid = row.ic.Status == IncidentChargeStatus.Charged ? row.ic.Amount : 0m;
            var remaining = isSettled ? 0m : row.ic.Amount;

            penalties.Add(new ContractPenaltyDto(
                row.ic.Id,
                "INCIDENT_CHARGE",
                row.ic.RentalChargeId,
                row.ic.Id,
                row.ic.ChargeType,
                string.IsNullOrWhiteSpace(row.ic.Description)
                    ? $"Phí sự cố ({row.ic.ChargeType})"
                    : row.ic.Description,
                row.IncidentCode,
                row.SerialNumber,
                row.ic.Amount,
                paid,
                remaining,
                EnumName.ToDatabaseValue(row.ic.Status),
                IsOffsetByDeposit: false,
                row.ic.CreatedAt,
                PaidAt: null,
                Note: null,
                pendingCashTransactions.Any(t => t.IncidentChargeId == row.ic.Id)));
        }

        var depositRemaining = Math.Max(0m, contract.DepositRequiredAmount - contract.DepositPaidAmount);
        var installmentsTotal = installments.Sum(i => i.Amount);
        var installmentsPaid = installments.Sum(i => i.PaidAmount);
        var installmentsRemaining = installments
            .Where(i => i.Status is "UNPAID" or "PARTIALLY_PAID")
            .Sum(i => i.RemainingAmount);

        var penaltiesTotal = penalties.Where(p => p.Status != "WAIVED").Sum(p => p.Amount);
        var penaltiesPaid = penalties.Sum(p => p.PaidAmount);
        var penaltiesRemaining = penalties
            .Where(p => p.Status is "UNPAID" or "PARTIALLY_PAID" or "PENDING")
            .Sum(p => p.RemainingAmount);

        var totalOutstanding = depositRemaining + installmentsRemaining + penaltiesRemaining;
        var mappedTransactions = await ToTransactionResponsesAsync(transactions, contract.ContractCode, cancellationToken);

        return new ContractBillingSummaryResponse(
            contract.Id,
            contract.ContractCode,
            contract.RentalType,
            contract.StartDate,
            contract.EndDate,
            days,
            equipmentRentalTotal,
            servicesTotal,
            contract.ContractTotalAmount,
            contract.DepositRequiredAmount,
            contract.DepositPaidAmount,
            contract.DepositAppliedAmount,
            contract.DepositRefundedAmount,
            depositRemaining,
            pendingCashTransactions.Any(t => t.TargetType == PaymentTargetType.Deposit),
            installmentsTotal,
            installmentsPaid,
            installmentsRemaining,
            penaltiesTotal,
            penaltiesPaid,
            penaltiesRemaining,
            totalOutstanding,
            installments,
            penalties,
            mappedTransactions);
    }

    // ================================================================ VNPAY Sandbox

    public async Task<PaymentTransactionResponse> CreateVnpayPaymentAsync(
        CreateVnpayPaymentRequest request,
        Guid currentUserId,
        bool isCustomer,
        bool canViewAll,
        string clientIp,
        string fallbackReturnUrl,
        bool isTechnicianOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_vnpay.TmnCode) || string.IsNullOrWhiteSpace(_vnpay.HashSecret))
        {
            throw new AppException(
                "VNPAY_NOT_CONFIGURED",
                "Cổng thanh toán VNPAY chưa được cấu hình (TmnCode / HashSecret).",
                StatusCodes.Status500InternalServerError);
        }

        var contractId = request.RentalContractId!.Value;
        var contract = await EnsureContractAccessAsync(contractId, currentUserId, isCustomer, canViewAll, cancellationToken);
        await EnsureRentalChargesGeneratedAsync(contract, currentUserId, cancellationToken);

        if (!PayableContractStatuses.Contains(contract.Status))
        {
            throw new AppException(
                "CONTRACT_NOT_PAYABLE",
                "Hợp đồng cần được Quản lý duyệt bản PDF đã ký (chuyển sang Chờ đặt cọc) mới có thể thanh toán.",
                StatusCodes.Status409Conflict);
        }

        if (isTechnicianOnly)
        {
            await EnsureTechnicianCanCollectPaymentAsync(contract.Id, currentUserId, cancellationToken);
        }

        var targetType = ParseTargetType(request.TargetType);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var resolved = await ResolvePayableTargetAsync(
            contract, targetType, request.RentalChargeId, request.IncidentChargeId, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // Kiểm tra các giao dịch VNPAY PENDING cũ của cùng mục thanh toán:
        // - Nếu đã hết hạn (ExpiresAt <= now) -> chuyển EXPIRED.
        // - Nếu VẪN CÒN HẠN và đúng số tiền cần thu -> TÁI SỬ DỤNG luôn giao dịch cũ (không tự hủy khi người dùng nhấn ra ngoài)!
        // - Chỉ sinh giao dịch mới khi không còn giao dịch nào đang chạy (hoặc người dùng đã tự bấm Hủy / số tiền thay đổi).
        var existingPending = await db.PaymentTransactions
            .Where(t =>
                t.RentalContractId == contract.Id &&
                t.PaymentMethod == PaymentMethod.Vnpay &&
                t.Status == PaymentTransactionStatus.Pending &&
                t.TargetType == targetType &&
                t.RentalChargeId == resolved.RentalChargeId &&
                t.IncidentChargeId == resolved.IncidentChargeId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        PaymentTransaction? reusable = null;
        var mutatedOld = false;

        foreach (var old in existingPending)
        {
            if (old.ExpiresAt.HasValue && old.ExpiresAt.Value <= now)
            {
                old.Status = PaymentTransactionStatus.Expired;
                old.UpdatedAt = now;
                mutatedOld = true;
            }
            else if (reusable is null
                     && old.Amount == resolved.RemainingAmount
                     && !string.IsNullOrWhiteSpace(old.PaymentUrl))
            {
                reusable = old;
            }
            else
            {
                old.Status = PaymentTransactionStatus.Cancelled;
                old.UpdatedAt = now;
                mutatedOld = true;
            }
        }

        if (reusable is not null)
        {
            if (mutatedOld)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return (await ToTransactionResponsesAsync([reusable], contract.ContractCode, cancellationToken)).Single();
        }

        var expireMinutes = _vnpay.ExpireMinutes > 0 ? _vnpay.ExpireMinutes : 15;
        var expiresAt = now.AddMinutes(expireMinutes);
        var txnRef = GenerateUniqueTxnRef(targetType);
        var returnUrl = !string.IsNullOrWhiteSpace(_vnpay.ReturnUrl)
            ? _vnpay.ReturnUrl.Trim()
            : fallbackReturnUrl;

        var paymentUrl = VnpayLibrary.BuildPaymentUrl(
            _vnpay,
            txnRef,
            resolved.RemainingAmount,
            resolved.OrderInfo,
            clientIp,
            returnUrl,
            now,
            expiresAt);

        var entity = new PaymentTransaction
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contract.Id,
            RentalChargeId = resolved.RentalChargeId,
            IncidentChargeId = resolved.IncidentChargeId,
            TargetType = targetType,
            PaymentMethod = PaymentMethod.Vnpay,
            TxnRef = txnRef,
            Amount = resolved.RemainingAmount,
            OrderInfo = resolved.OrderInfo,
            Status = PaymentTransactionStatus.Pending,
            PaymentUrl = paymentUrl,
            ExpiresAt = expiresAt,
            CreatedBy = currentUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.PaymentTransactions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return (await ToTransactionResponsesAsync([entity], contract.ContractCode, cancellationToken)).Single();
    }

    public async Task<PaymentTransactionResponse> GetTransactionStatusAsync(
        Guid transactionId,
        Guid currentUserId,
        bool isCustomer,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.PaymentTransactions
            .SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken)
            ?? throw new AppException("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.", StatusCodes.Status404NotFound);

        var contract = await EnsureContractAccessAsync(
            entity.RentalContractId, currentUserId, isCustomer, canViewAll, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (entity.PaymentMethod == PaymentMethod.Vnpay &&
            entity.Status == PaymentTransactionStatus.Pending &&
            entity.ExpiresAt.HasValue &&
            entity.ExpiresAt.Value <= now)
        {
            entity.Status = PaymentTransactionStatus.Expired;
            entity.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await ToTransactionResponsesAsync([entity], contract.ContractCode, cancellationToken)).Single();
    }

    public async Task<PaymentTransactionResponse> CancelVnpayTransactionAsync(
        Guid transactionId,
        Guid currentUserId,
        bool isCustomer,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        var entity = await db.PaymentTransactions
            .SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken)
            ?? throw new AppException("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch thanh toán.", StatusCodes.Status404NotFound);

        var contract = await EnsureContractAccessAsync(
            entity.RentalContractId, currentUserId, isCustomer, canViewAll, cancellationToken);

        if (entity.PaymentMethod != PaymentMethod.Vnpay)
        {
            throw new AppException("INVALID_PAYMENT_METHOD", "Chỉ hủy được giao dịch VNPAY đang chờ.", StatusCodes.Status400BadRequest);
        }

        if (entity.Status == PaymentTransactionStatus.Pending)
        {
            entity.Status = PaymentTransactionStatus.Cancelled;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await ToTransactionResponsesAsync([entity], contract.ContractCode, cancellationToken)).Single();
    }

    public sealed record VnpayCallbackResult(
        string RspCode,
        string Message,
        bool IsSuccess,
        bool IsCancelled,
        string? TxnRef,
        decimal? Amount,
        string? OrderInfo);

    /// <summary>
    /// Xử lý IPN và ReturnUrl từ VNPAY Sandbox (cùng logic chống ghi trùng Idempotent).
    /// - Kiểm tra chữ ký HMAC-SHA512.
    /// - Kiểm tra mã giao dịch &amp; số tiền.
    /// - Bắt mã 24 -> CANCELLED; 00/00 -> VERIFIED + gạch nợ tự động.
    /// </summary>
    public async Task<VnpayCallbackResult> ProcessVnpayCallbackAsync(
        IQueryCollection query,
        CancellationToken cancellationToken = default)
    {
        if (!VnpayLibrary.ValidateSignature(query, _vnpay.HashSecret))
        {
            logger.LogWarning("VNPAY callback rejected: invalid signature.");
            return new VnpayCallbackResult("97", "Invalid signature", false, false, null, null, null);
        }

        var txnRef = query["vnp_TxnRef"].ToString().Trim();
        var rawAmount = query["vnp_Amount"].ToString().Trim();
        var responseCode = query["vnp_ResponseCode"].ToString().Trim();
        var transactionStatus = query["vnp_TransactionStatus"].ToString().Trim();
        var vnpTransactionNo = query["vnp_TransactionNo"].ToString().Trim();
        var vnpBankCode = query["vnp_BankCode"].ToString().Trim();
        var vnpPayDate = query["vnp_PayDate"].ToString().Trim();

        if (string.IsNullOrWhiteSpace(txnRef))
            return new VnpayCallbackResult("01", "Order not found", false, false, null, null, null);

        await using var dbTx = await db.Database.BeginTransactionAsync(cancellationToken);

        // Khóa dòng giao dịch để đảm bảo tuyệt đối không gạch nợ 2 lần khi IPN & ReturnUrl tới đồng thời.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM payment_transactions WHERE txn_ref = {txnRef} FOR UPDATE",
            cancellationToken);

        var txn = await db.PaymentTransactions
            .SingleOrDefaultAsync(t => t.TxnRef == txnRef, cancellationToken);

        if (txn is null)
            return new VnpayCallbackResult("01", "Order not found", false, false, txnRef, null, null);

        if (!long.TryParse(rawAmount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amountTimes100) ||
            amountTimes100 != (long)Math.Round(txn.Amount * 100m, MidpointRounding.AwayFromZero))
        {
            logger.LogWarning("VNPAY amount mismatch for {TxnRef}: got {Got}, expected {Expected}",
                txnRef, rawAmount, txn.Amount * 100m);
            return new VnpayCallbackResult("04", "Invalid amount", false, false, txnRef, txn.Amount, txn.OrderInfo);
        }

        // Idempotency: đã xác nhận thành công trước đó.
        if (txn.Status == PaymentTransactionStatus.Verified)
        {
            return new VnpayCallbackResult("02", "Order already confirmed", true, false, txnRef, txn.Amount, txn.OrderInfo);
        }

        var now = DateTimeOffset.UtcNow;
        txn.VnpResponseCode = responseCode;
        txn.VnpTransactionNo = string.IsNullOrWhiteSpace(vnpTransactionNo) ? txn.VnpTransactionNo : vnpTransactionNo;
        txn.VnpBankCode = string.IsNullOrWhiteSpace(vnpBankCode) ? txn.VnpBankCode : vnpBankCode;
        txn.VnpPayDate = string.IsNullOrWhiteSpace(vnpPayDate) ? txn.VnpPayDate : vnpPayDate;
        txn.UpdatedAt = now;

        // Thành công khi cả vnp_ResponseCode == "00" và (vnp_TransactionStatus == "00" hoặc rỗng ở một số bản sandbox).
        var isSuccess = responseCode == "00" &&
                        (string.IsNullOrEmpty(transactionStatus) || transactionStatus == "00");

        if (isSuccess)
        {
            txn.Status = PaymentTransactionStatus.Verified;
            txn.VerifiedAt = now;

            var movedToPreparing = await ApplyVerifiedPaymentAsync(txn, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await dbTx.CommitAsync(cancellationToken);

            if (movedToPreparing)
            {
                await NotifyDepositCompletedAndPreparingAsync(txn.RentalContractId, txn.TxnRef, cancellationToken);
            }

            return new VnpayCallbackResult("00", "Confirm Success", true, false, txnRef, txn.Amount, txn.OrderInfo);
        }

        // Mã 24: Khách hàng hủy giao dịch trên trang VNPAY.
        var isCancelled = responseCode == "24";
        if (txn.Status == PaymentTransactionStatus.Pending)
        {
            txn.Status = isCancelled ? PaymentTransactionStatus.Cancelled : PaymentTransactionStatus.Failed;
            await db.SaveChangesAsync(cancellationToken);
            await dbTx.CommitAsync(cancellationToken);
        }

        return new VnpayCallbackResult("00", "Confirm Success", false, isCancelled, txnRef, txn.Amount, txn.OrderInfo);
    }

    // ================================================================ Tiền mặt (CASH)

    public Task<FileUploadTicket> CreateCashReceiptUploadTicketAsync(
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.CreateUploadTicketAsync(
            CashReceiptUploadPolicy,
            new FileUploadRequest { FileCount = 1 },
            currentUserId,
            cancellationToken);

    public Task<ConfirmFileUploadResponse> ConfirmCashReceiptUploadAsync(
        ConfirmFileUploadRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.ConfirmUploadAsync(request, currentUserId, cancellationToken);

    /// <summary>Ghi nhận thu tiền mặt (có ảnh phiếu thu/biên bản ký hai bên), trạng thái PENDING chờ Manager duyệt.</summary>
    public async Task<PaymentTransactionResponse> CreateCashPaymentAsync(
        CreateCashPaymentRequest request,
        Guid currentUserId,
        bool canViewAll,
        bool isTechnicianOnly = false,
        CancellationToken cancellationToken = default)
    {
        var contract = await EnsureContractAccessAsync(
            request.RentalContractId!.Value, currentUserId, isCustomer: false, canViewAll, cancellationToken);
        await EnsureRentalChargesGeneratedAsync(contract, currentUserId, cancellationToken);

        if (!PayableContractStatuses.Contains(contract.Status))
        {
            throw new AppException("CONTRACT_NOT_PAYABLE", "Hợp đồng cần được Quản lý duyệt bản PDF đã ký (chuyển sang Chờ đặt cọc) mới có thể thu tiền.", StatusCodes.Status409Conflict);
        }

        if (isTechnicianOnly)
        {
            await EnsureTechnicianCanCollectPaymentAsync(contract.Id, currentUserId, cancellationToken);
        }

        Guid? validatedReceiptFileId = null;
        if (request.ReceiptFileId.HasValue)
        {
            var receiptFile = await db.StorageFiles
                .AsNoTracking()
                .SingleOrDefaultAsync(f => f.Id == request.ReceiptFileId.Value, cancellationToken);

            if (receiptFile is null || receiptFile.UploadedBy != currentUserId ||
                !CashReceiptFormats.Contains((receiptFile.ContentType ?? "").ToLowerInvariant()))
            {
                throw new AppException(
                    "RECEIPT_FILE_INVALID",
                    "Ảnh phiếu thu không hợp lệ hoặc không phải do bạn tải lên.",
                    StatusCodes.Status400BadRequest,
                    "receiptFileId");
            }
            validatedReceiptFileId = receiptFile.Id;
        }

        var targetType = ParseTargetType(request.TargetType);
        var resolved = await ResolvePayableTargetAsync(
            contract, targetType, request.RentalChargeId, request.IncidentChargeId, cancellationToken);

        if (request.Amount > resolved.RemainingAmount)
        {
            throw new AppException(
                "PAYMENT_EXCEEDS_REMAINING",
                $"Số tiền thu ({request.Amount:N0} đ) vượt quá số tiền còn phải thanh toán ({resolved.RemainingAmount:N0} đ).",
                StatusCodes.Status400BadRequest,
                "amount");
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new PaymentTransaction
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contract.Id,
            RentalChargeId = resolved.RentalChargeId,
            IncidentChargeId = resolved.IncidentChargeId,
            TargetType = targetType,
            PaymentMethod = PaymentMethod.Cash,
            TxnRef = GenerateUniqueTxnRef(targetType, isCash: true),
            Amount = request.Amount,
            OrderInfo = resolved.OrderInfo,
            Status = PaymentTransactionStatus.Pending,
            ReceiptFileId = validatedReceiptFileId,
            CreatedBy = currentUserId,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.PaymentTransactions.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        // Thông báo cho Manager biết có hóa đơn thu tiền mặt mới gắn nhân viên (Sales / KTV) đang chờ duyệt
        var collectorName = await db.Users.AsNoTracking()
            .Where(u => u.Id == currentUserId)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Nhân viên";

        var managerIds = new List<Guid>();
        if (contract.ApprovedBy.HasValue)
        {
            managerIds.Add(contract.ApprovedBy.Value);
        }
        else
        {
            managerIds.AddRange(await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER"
                select ur.UserId
            ).Distinct().ToListAsync(cancellationToken));
        }

        if (managerIds.Count > 0)
        {
            var targetLabel = targetType == PaymentTargetType.Deposit ? "tiền đặt cọc" : "thanh toán";
            await notificationDispatcher.NotifyUsersAsync(
                managerIds.Distinct(),
                $"Hóa đơn thu {targetLabel} chờ duyệt ({contract.ContractCode})",
                $"{collectorName} đã xác nhận nhận trực tiếp {entity.Amount:N0} đ cho hợp đồng {contract.ContractCode} (Mã hóa đơn: {entity.TxnRef}). Vui lòng kiểm tra và duyệt hóa đơn.",
                "CASH_PAYMENT_PENDING",
                cancellationToken: cancellationToken);
        }

        return (await ToTransactionResponsesAsync([entity], contract.ContractCode, cancellationToken)).Single();
    }

    public async Task<List<PaymentTransactionResponse>> GetPendingCashPaymentsAsync(
        Guid managerUserId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from t in db.PaymentTransactions.AsNoTracking()
            join c in db.RentalContracts.AsNoTracking() on t.RentalContractId equals c.Id
            where t.PaymentMethod == PaymentMethod.Cash
                  && t.Status == PaymentTransactionStatus.Pending
                  && c.ApprovedBy == managerUserId
            orderby t.CreatedAt
            select new { t, c.ContractCode }
        ).ToListAsync(cancellationToken);

        var result = new List<PaymentTransactionResponse>(rows.Count);
        foreach (var group in rows.GroupBy(r => r.ContractCode))
        {
            result.AddRange(await ToTransactionResponsesAsync(
                group.Select(g => g.t).ToList(), group.Key, cancellationToken));
        }

        return result;
    }

    /// <summary>Manager xác nhận phiếu thu tiền mặt (PENDING → VERIFIED) và gạch nợ.</summary>
    public async Task<PaymentTransactionResponse> VerifyCashPaymentAsync(
        Guid transactionId,
        Guid managerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var dbTx = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM payment_transactions WHERE id = {transactionId} FOR UPDATE",
            cancellationToken);

        var txn = await db.PaymentTransactions
            .SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken)
            ?? throw new AppException("PAYMENT_NOT_FOUND", "Không tìm thấy phiếu thu.", StatusCodes.Status404NotFound);

        if (txn.PaymentMethod != PaymentMethod.Cash || txn.Status != PaymentTransactionStatus.Pending)
        {
            throw new AppException("PAYMENT_NOT_PENDING_CASH", "Chỉ duyệt được phiếu thu tiền mặt đang chờ xác nhận.", StatusCodes.Status409Conflict);
        }

        var contractOwner = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == txn.RentalContractId)
            .Select(c => new { c.Id, c.ContractCode, c.ApprovedBy })
            .SingleAsync(cancellationToken);

        if (contractOwner.ApprovedBy.HasValue && contractOwner.ApprovedBy.Value != managerUserId)
        {
            throw new AppException(
                "FORBIDDEN_MANAGER",
                $"Hợp đồng {contractOwner.ContractCode} do Quản lý khác phụ trách, bạn không có quyền duyệt hóa đơn thu tiền này.",
                StatusCodes.Status403Forbidden);
        }

        var now = DateTimeOffset.UtcNow;
        txn.Status = PaymentTransactionStatus.Verified;
        txn.VerifiedBy = managerUserId;
        txn.VerifiedAt = now;
        txn.UpdatedAt = now;

        var movedToPreparing = await ApplyVerifiedPaymentAsync(txn, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await dbTx.CommitAsync(cancellationToken);

        if (movedToPreparing)
        {
            await NotifyDepositCompletedAndPreparingAsync(txn.RentalContractId, txn.TxnRef, cancellationToken);
        }

        var contractCode = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == txn.RentalContractId)
            .Select(c => c.ContractCode)
            .SingleAsync(cancellationToken);

        await notificationDispatcher.NotifyUsersAsync(
            [txn.CreatedBy],
            $"Đã duyệt hóa đơn {txn.TxnRef} ({contractCode})",
            $"Quản lý đã duyệt hóa đơn thu trực tiếp {txn.TxnRef} ({txn.Amount:N0} đ) của hợp đồng {contractCode}.",
            "CASH_PAYMENT_VERIFIED",
            cancellationToken: cancellationToken);

        return (await ToTransactionResponsesAsync([txn], contractCode, cancellationToken)).Single();
    }

    /// <summary>Manager từ chối phiếu thu tiền mặt (PENDING → FAILED).</summary>
    public async Task<PaymentTransactionResponse> RejectCashPaymentAsync(
        Guid transactionId,
        RejectCashPaymentRequest request,
        Guid managerUserId,
        CancellationToken cancellationToken = default)
    {
        var txn = await db.PaymentTransactions
            .SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken)
            ?? throw new AppException("PAYMENT_NOT_FOUND", "Không tìm thấy phiếu thu.", StatusCodes.Status404NotFound);

        if (txn.PaymentMethod != PaymentMethod.Cash || txn.Status != PaymentTransactionStatus.Pending)
        {
            throw new AppException("PAYMENT_NOT_PENDING_CASH", "Chỉ từ chối được phiếu thu tiền mặt đang chờ xác nhận.", StatusCodes.Status409Conflict);
        }

        var contractOwner = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == txn.RentalContractId)
            .Select(c => new { c.ContractCode, c.ApprovedBy })
            .SingleAsync(cancellationToken);

        if (contractOwner.ApprovedBy.HasValue && contractOwner.ApprovedBy.Value != managerUserId)
        {
            throw new AppException(
                "FORBIDDEN_MANAGER",
                $"Hợp đồng {contractOwner.ContractCode} do Quản lý khác phụ trách, bạn không có quyền từ chối hóa đơn thu tiền này.",
                StatusCodes.Status403Forbidden);
        }

        var now = DateTimeOffset.UtcNow;
        txn.Status = PaymentTransactionStatus.Failed;
        txn.VerifiedBy = managerUserId;
        txn.VerifiedAt = now;
        txn.RejectedReason = request.Reason.Trim();
        txn.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        var contractCode = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == txn.RentalContractId)
            .Select(c => c.ContractCode)
            .SingleAsync(cancellationToken);

        await notificationDispatcher.NotifyUsersAsync(
            [txn.CreatedBy],
            $"Từ chối hóa đơn {txn.TxnRef} ({contractCode})",
            $"Quản lý đã từ chối hóa đơn thu trực tiếp {txn.TxnRef} ({txn.Amount:N0} đ) của hợp đồng {contractCode}. Lý do: {txn.RejectedReason}",
            "CASH_PAYMENT_REJECTED",
            cancellationToken: cancellationToken);

        return (await ToTransactionResponsesAsync([txn], contractCode, cancellationToken)).Single();
    }

    // ================================================================ gạch nợ & helpers

    /// <summary>Gạch nợ sau khi thanh toán thành công; trả về true nếu hợp đồng vừa đủ cọc và tự chuyển PENDING_DEPOSIT → PREPARING.</summary>
    private async Task<bool> ApplyVerifiedPaymentAsync(
        PaymentTransaction txn,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var movedToPreparing = false;

        switch (txn.TargetType)
        {
            case PaymentTargetType.Deposit:
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM rental_contracts WHERE id = {txn.RentalContractId} FOR UPDATE",
                        cancellationToken);

                    var contract = await db.RentalContracts.SingleAsync(c => c.Id == txn.RentalContractId, cancellationToken);
                    contract.DepositPaidAmount += txn.Amount;
                    contract.UpdatedAt = now;

                    if (contract.Status == RentalContractStatus.PendingDeposit
                        && contract.DepositPaidAmount >= contract.DepositRequiredAmount)
                    {
                        contract.Status = RentalContractStatus.Preparing;
                        db.ContractStatusHistory.Add(new ContractStatusHistory
                        {
                            Id = Guid.CreateVersion7(),
                            RentalContractId = contract.Id,
                            PreviousStatus = RentalContractStatus.PendingDeposit,
                            Status = RentalContractStatus.Preparing,
                            ChangedBy = txn.VerifiedBy ?? txn.CreatedBy,
                            ChangedAt = now,
                            Note = $"Đã nhận đủ tiền đặt cọc ({txn.TxnRef}) — Tự động chuyển sang chuẩn bị thiết bị.",
                        });
                        movedToPreparing = true;
                    }
                    break;
                }

            case PaymentTargetType.RentalInstallment:
            case PaymentTargetType.Charge:
                {
                    var chargeId = txn.RentalChargeId!.Value;
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM rental_charges WHERE id = {chargeId} FOR UPDATE",
                        cancellationToken);

                    var charge = await db.RentalCharges.SingleAsync(c => c.Id == chargeId, cancellationToken);
                    charge.PaidAmount = Math.Min(charge.Amount, charge.PaidAmount + txn.Amount);
                    if (charge.PaidAmount >= charge.Amount)
                    {
                        charge.Status = RentalChargeStatus.Paid;
                        charge.PaidAt = now;
                    }
                    else
                    {
                        charge.Status = RentalChargeStatus.PartiallyPaid;
                    }

                    // Đồng bộ incident_charge liên kết CHỈ KHI khoản thu đã thanh toán đủ 100%
                    if (charge.Status == RentalChargeStatus.Paid)
                    {
                        var linkedIncidentCharges = await db.IncidentCharges
                            .Where(ic => ic.RentalChargeId == charge.Id && ic.Status == IncidentChargeStatus.Pending)
                            .ToListAsync(cancellationToken);

                        foreach (var ic in linkedIncidentCharges)
                            ic.Status = IncidentChargeStatus.Charged;
                    }

                    break;
                }

            case PaymentTargetType.IncidentCharge:
                {
                    if (txn.RentalChargeId.HasValue)
                    {
                        var rc = await db.RentalCharges.SingleOrDefaultAsync(c => c.Id == txn.RentalChargeId.Value, cancellationToken);
                        if (rc is not null)
                        {
                            rc.PaidAmount = Math.Min(rc.Amount, rc.PaidAmount + txn.Amount);
                            rc.Status = rc.PaidAmount >= rc.Amount ? RentalChargeStatus.Paid : RentalChargeStatus.PartiallyPaid;
                            if (rc.Status == RentalChargeStatus.Paid)
                                rc.PaidAt = now;
                        }
                    }

                    if (txn.IncidentChargeId.HasValue)
                    {
                        var ic = await db.IncidentCharges.SingleOrDefaultAsync(x => x.Id == txn.IncidentChargeId.Value, cancellationToken);
                        if (ic is not null)
                        {
                            var rcFullyPaid = !txn.RentalChargeId.HasValue || await db.RentalCharges
                                .AnyAsync(c => c.Id == txn.RentalChargeId.Value && c.Status == RentalChargeStatus.Paid, cancellationToken);
                            if (rcFullyPaid)
                                ic.Status = IncidentChargeStatus.Charged;
                        }
                    }
                    break;
                }
        }

        return movedToPreparing;
    }

    private async Task NotifyDepositCompletedAndPreparingAsync(
        Guid contractId,
        string txnRef,
        CancellationToken cancellationToken)
    {
        try
        {
            var contract = await db.RentalContracts.AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == contractId, cancellationToken);
            if (contract is null) return;

            var msg = $"Hợp đồng {contract.ContractCode} đã hoàn tất tiền đặt cọc ({txnRef}) và tự động chuyển sang trạng thái Chuẩn bị giao hàng (PREPARING).";

            await notificationDispatcher.NotifyCompanyAsync(
                contract.CompanyId,
                $"Đã nhận đủ cọc — Bắt đầu chuẩn bị ({contract.ContractCode})",
                msg,
                "CONTRACT_DEPOSIT_COMPLETED",
                cancellationToken: cancellationToken);

            var recipients = new List<Guid> { contract.CreatedBy };
            if (contract.ApprovedBy.HasValue) recipients.Add(contract.ApprovedBy.Value);

            var storekeeperIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "LEAD_STOREKEEPER" || r.Code == "STOREKEEPER"
                select ur.UserId
            ).Distinct().ToListAsync(cancellationToken);
            recipients.AddRange(storekeeperIds);

            await notificationDispatcher.NotifyUsersAsync(
                recipients.Distinct(),
                $"Hợp đồng {contract.ContractCode} đã đủ cọc — Sẵn sàng soạn kho",
                msg,
                "CONTRACT_DEPOSIT_COMPLETED",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không gửi được thông báo hoàn tất cọc cho hợp đồng {ContractId}.", contractId);
        }
    }

    /// <summary>
    /// Ràng buộc thu tiền phía Kỹ thuật viên:
    /// Chỉ KTV trưởng (LEADER) mới được tạo mã QR / nhận tiền mặt KHI VÀ CHỈ KHI phiếu bàn giao của hợp đồng
    /// đã được Quản lý duyệt xuất kho (SHIPPING) và chuyến giao đã xuất phát (IN_PROGRESS), hoặc đã bàn giao xong (COMPLETED).
    /// </summary>
    private async Task EnsureTechnicianCanCollectPaymentAsync(
        Guid contractId,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        var staffId = await db.Staff.AsNoTracking()
            .Where(s => s.UserId == currentUserId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (!staffId.HasValue)
        {
            throw new AppException(
                "FORBIDDEN_TECHNICIAN_PAYMENT",
                "Tài khoản không có hồ sơ kỹ thuật viên hợp lệ.",
                StatusCodes.Status403Forbidden);
        }

        var canCollect = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join sa in db.ServiceAssignments.AsNoTracking() on hn.ServiceAssignmentId equals sa.Id
            join m in db.ServiceAssignmentMembers.AsNoTracking() on sa.Id equals m.ServiceAssignmentId
            where hn.RentalContractId == contractId
                  && m.StaffId == staffId.Value
                  && m.Role == "LEADER"
                  && ((hn.Status == HandoverNoteStatus.Shipping && sa.Status == ServiceAssignmentStatus.InProgress)
                      || hn.Status == HandoverNoteStatus.Completed)
            select hn.Id
        ).AnyAsync(cancellationToken);

        if (!canCollect)
        {
            throw new AppException(
                "HANDOVER_NOT_READY_FOR_PAYMENT",
                "Chỉ KTV trưởng mới được tạo mã QR hoặc nhận tiền sau khi phiếu bàn giao đã được Quản lý duyệt xuất kho và chuyến giao đã xuất phát (hoặc đã giao hàng xong).",
                StatusCodes.Status409Conflict);
        }
    }

    private sealed record ResolvedPayableTarget(
        Guid? RentalChargeId,
        Guid? IncidentChargeId,
        decimal RemainingAmount,
        string OrderInfo);

    private async Task<ResolvedPayableTarget> ResolvePayableTargetAsync(
        RentalContract contract,
        PaymentTargetType targetType,
        Guid? rentalChargeId,
        Guid? incidentChargeId,
        CancellationToken cancellationToken)
    {
        switch (targetType)
        {
            case PaymentTargetType.Deposit:
                {
                    var remaining = contract.DepositRequiredAmount - contract.DepositPaidAmount;
                    if (remaining <= 0)
                        throw new AppException("ALREADY_PAID", "Khoản đặt cọc đã được thanh toán đủ.", StatusCodes.Status409Conflict);

                    return new ResolvedPayableTarget(
                        null,
                        null,
                        remaining,
                        VnpayLibrary.SanitizeOrderInfo($"DAT COC {contract.ContractCode}"));
                }

            case PaymentTargetType.RentalInstallment:
            case PaymentTargetType.Charge:
                {
                    if (rentalChargeId is null)
                        throw new AppException("CHARGE_ID_REQUIRED", "Vui lòng chọn khoản thu cần thanh toán.", StatusCodes.Status400BadRequest, "rentalChargeId");

                    var charge = await db.RentalCharges
                        .AsNoTracking()
                        .SingleOrDefaultAsync(c => c.Id == rentalChargeId.Value && c.RentalContractId == contract.Id, cancellationToken)
                        ?? throw new AppException("CHARGE_NOT_FOUND", "Không tìm thấy khoản thanh toán thuộc hợp đồng này.", StatusCodes.Status404NotFound);

                    if (charge.Status is RentalChargeStatus.Paid or RentalChargeStatus.Waived or RentalChargeStatus.Cancelled)
                        throw new AppException("ALREADY_PAID", "Khoản thu này đã được thanh toán hoặc miễn giảm.", StatusCodes.Status409Conflict);

                    var remaining = charge.Amount - charge.PaidAmount;
                    if (remaining <= 0)
                        throw new AppException("ALREADY_PAID", "Khoản thu này đã thanh toán đủ.", StatusCodes.Status409Conflict);

                    string orderInfo;
                    if (charge.ChargeType == "RENTAL")
                    {
                        var installmentOrder = await db.RentalCharges.AsNoTracking()
                            .Where(c => c.RentalContractId == contract.Id && c.ChargeType == "RENTAL" && c.Status != RentalChargeStatus.Cancelled)
                            .OrderBy(c => c.PeriodStart).ThenBy(c => c.CreatedAt)
                            .Select(c => c.Id)
                            .ToListAsync(cancellationToken);

                        var idx = Math.Max(1, installmentOrder.IndexOf(charge.Id) + 1);
                        orderInfo = VnpayLibrary.SanitizeOrderInfo($"TIEN THUE DOT {idx} {contract.ContractCode}");
                    }
                    else
                    {
                        orderInfo = VnpayLibrary.SanitizeOrderInfo($"PHAT {charge.ChargeType} {contract.ContractCode}");
                    }

                    return new ResolvedPayableTarget(charge.Id, incidentChargeId, remaining, orderInfo);
                }

            case PaymentTargetType.IncidentCharge:
                {
                    if (incidentChargeId is null)
                        throw new AppException("INCIDENT_CHARGE_ID_REQUIRED", "Vui lòng chọn khoản phí sự cố.", StatusCodes.Status400BadRequest, "incidentChargeId");

                    var row = await (
                        from ic in db.IncidentCharges.AsNoTracking()
                        join ii in db.EquipmentIncidentItems.AsNoTracking() on ic.IncidentItemId equals ii.Id
                        join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
                        where ic.Id == incidentChargeId.Value && inc.CompanyId == contract.CompanyId
                        select new { ic, inc.IncidentCode }
                    ).SingleOrDefaultAsync(cancellationToken)
                        ?? throw new AppException("INCIDENT_CHARGE_NOT_FOUND", "Không tìm thấy khoản phí sự cố.", StatusCodes.Status404NotFound);

                    if (row.ic.RentalChargeId.HasValue)
                    {
                        var rc = await db.RentalCharges.AsNoTracking()
                            .SingleOrDefaultAsync(c => c.Id == row.ic.RentalChargeId.Value, cancellationToken);
                        if (rc is not null)
                        {
                            var remaining = rc.Amount - rc.PaidAmount;
                            if (rc.Status is RentalChargeStatus.Paid or RentalChargeStatus.Waived or RentalChargeStatus.Cancelled || remaining <= 0)
                                throw new AppException("ALREADY_PAID", "Khoản phí sự cố đã được thanh toán.", StatusCodes.Status409Conflict);

                            return new ResolvedPayableTarget(
                                rc.Id,
                                row.ic.Id,
                                remaining,
                                VnpayLibrary.SanitizeOrderInfo($"PHI SU CO {row.IncidentCode} {contract.ContractCode}"));
                        }
                    }

                    if (row.ic.Status != IncidentChargeStatus.Pending || row.ic.Amount <= 0)
                        throw new AppException("ALREADY_PAID", "Khoản phí sự cố không còn cần thanh toán.", StatusCodes.Status409Conflict);

                    return new ResolvedPayableTarget(
                        null,
                        row.ic.Id,
                        row.ic.Amount,
                        VnpayLibrary.SanitizeOrderInfo($"PHI SU CO {row.IncidentCode} {contract.ContractCode}"));
                }

            default:
                throw new AppException("INVALID_TARGET_TYPE", "Loại khoản thanh toán không hợp lệ.", StatusCodes.Status400BadRequest);
        }
    }

    private async Task ExpireTimedOutTransactionsAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.PaymentTransactions
            .Where(t =>
                t.RentalContractId == contractId &&
                t.PaymentMethod == PaymentMethod.Vnpay &&
                t.Status == PaymentTransactionStatus.Pending &&
                t.ExpiresAt != null &&
                t.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, PaymentTransactionStatus.Expired)
                .SetProperty(t => t.UpdatedAt, now), cancellationToken);
    }

    private async Task<RentalContract> EnsureContractAccessAsync(
        Guid contractId,
        Guid currentUserId,
        bool isCustomer,
        bool canViewAll,
        CancellationToken cancellationToken)
    {
        var contract = await db.RentalContracts
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == contractId, cancellationToken)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        if (isCustomer)
        {
            var companyId = await customerPortalService.GetCompanyIdAsync(currentUserId, cancellationToken);
            if (contract.CompanyId != companyId)
                throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);
        }
        else if (!canViewAll && contract.CreatedBy != currentUserId)
        {
            throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);
        }

        return contract;
    }

    private async Task<List<PaymentTransactionResponse>> ToTransactionResponsesAsync(
        List<PaymentTransaction> transactions,
        string contractCode,
        CancellationToken cancellationToken)
    {
        if (transactions.Count == 0)
            return [];

        var userIds = transactions
            .SelectMany(t => t.VerifiedBy.HasValue ? new[] { t.CreatedBy, t.VerifiedBy.Value } : new[] { t.CreatedBy })
            .Distinct()
            .ToList();

        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var fileIds = transactions
            .Where(t => t.ReceiptFileId.HasValue)
            .Select(t => t.ReceiptFileId!.Value)
            .Distinct()
            .ToList();

        var files = fileIds.Count == 0
            ? new Dictionary<Guid, StorageFile>()
            : await db.StorageFiles.AsNoTracking()
                .Where(f => fileIds.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, cancellationToken);

        return transactions.Select(t =>
        {
            string? receiptUrl = null;
            if (t.ReceiptFileId.HasValue && files.TryGetValue(t.ReceiptFileId.Value, out var file))
            {
                receiptUrl = uploadService.GetPrivateImageUrl(
                    $"{file.BucketName}/{file.ObjectKey}",
                    file.ResourceType.ToCloudinaryNetResourceType(),
                    file.ContentType!);
            }

            return new PaymentTransactionResponse(
                t.Id,
                t.RentalContractId,
                contractCode,
                t.RentalChargeId,
                t.IncidentChargeId,
                EnumName.ToDatabaseValue(t.TargetType),
                EnumName.ToDatabaseValue(t.PaymentMethod),
                t.TxnRef,
                t.Amount,
                t.OrderInfo,
                EnumName.ToDatabaseValue(t.Status),
                t.PaymentUrl,
                t.ExpiresAt,
                t.VnpTransactionNo,
                t.VnpBankCode,
                t.VnpResponseCode,
                receiptUrl,
                users.GetValueOrDefault(t.CreatedBy),
                t.VerifiedBy.HasValue ? users.GetValueOrDefault(t.VerifiedBy.Value) : null,
                t.VerifiedAt,
                t.RejectedReason,
                t.Note,
                t.CreatedAt);
        }).ToList();
    }

    private static PaymentTargetType ParseTargetType(string value)
    {
        try
        {
            return EnumName.FromDatabaseValue<PaymentTargetType>(value.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new AppException("INVALID_TARGET_TYPE", "Loại khoản thanh toán không hợp lệ.", StatusCodes.Status400BadRequest, "targetType");
        }
    }

    /// <summary>Sinh mã giao dịch duy nhất mỗi lần tạo QR/phiếu thu (≤ 30 ký tự chữ-số cho VNPAY).</summary>
    private static string GenerateUniqueTxnRef(PaymentTargetType targetType, bool isCash = false)
    {
        var prefix = isCash ? "CSH" : targetType switch
        {
            PaymentTargetType.Deposit => "DEP",
            PaymentTargetType.RentalInstallment => "RNT",
            PaymentTargetType.Charge => "CHG",
            PaymentTargetType.IncidentCharge => "INC",
            _ => "PAY",
        };

        var stamp = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        var randomHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        return $"{prefix}{stamp}{randomHex}";
    }

    private static string ChargeTypeFallbackLabel(string chargeType) => chargeType switch
    {
        "LATE_RETURN" => "Phí trả thiết bị trễ hạn",
        "EARLY_CANCELLATION" => "Phí hủy hợp đồng trước hạn",
        "DAMAGE" => "Bồi thường hư hỏng thiết bị",
        "LOSS" => "Bồi thường mất thiết bị",
        "TRANSPORT" => "Phí vận chuyển phát sinh",
        "LATE_PAYMENT" => "Phí chậm thanh toán",
        _ => "Khoản phí phát sinh khác",
    };
}
