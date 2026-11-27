using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Finance.DTOs;

public sealed class CreateVnpayPaymentRequest
{
    [Required(ErrorMessage = "Vui lòng chọn hợp đồng.")]
    public Guid? RentalContractId { get; init; }

    /// <summary>DEPOSIT | RENTAL_INSTALLMENT | CHARGE | INCIDENT_CHARGE</summary>
    [Required(ErrorMessage = "Vui lòng chọn loại khoản thanh toán.")]
    public string TargetType { get; init; } = string.Empty;

    public Guid? RentalChargeId { get; init; }
    public Guid? IncidentChargeId { get; init; }
}

public sealed class CreateCashPaymentRequest
{
    [Required(ErrorMessage = "Vui lòng chọn hợp đồng.")]
    public Guid? RentalContractId { get; init; }

    /// <summary>DEPOSIT | RENTAL_INSTALLMENT | CHARGE | INCIDENT_CHARGE</summary>
    [Required(ErrorMessage = "Vui lòng chọn loại khoản thanh toán.")]
    public string TargetType { get; init; } = string.Empty;

    public Guid? RentalChargeId { get; init; }
    public Guid? IncidentChargeId { get; init; }

    [Range(1000, 999_999_999_999, ErrorMessage = "Số tiền thu không hợp lệ.")]
    public decimal Amount { get; init; }

    /// <summary>Ảnh phiếu thu / biên bản thu tiền mặt đã ký hai bên (StorageFileId, tùy chọn khi Sales thu trực tiếp).</summary>
    public Guid? ReceiptFileId { get; init; }

    [MaxLength(1000)]
    public string? Note { get; init; }
}

public sealed class RejectCashPaymentRequest
{
    [Required(ErrorMessage = "Vui lòng nhập lý do từ chối.")]
    [MaxLength(1000)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record PaymentTransactionResponse(
    Guid Id,
    Guid RentalContractId,
    string ContractCode,
    Guid? RentalChargeId,
    Guid? IncidentChargeId,
    // DEPOSIT | RENTAL_INSTALLMENT | CHARGE | INCIDENT_CHARGE
    string TargetType,
    // VNPAY | CASH
    string PaymentMethod,
    string TxnRef,
    decimal Amount,
    string OrderInfo,
    // PENDING | VERIFIED | FAILED | CANCELLED | EXPIRED
    string Status,
    string? PaymentUrl,
    DateTimeOffset? ExpiresAt,
    string? VnpTransactionNo,
    string? VnpBankCode,
    string? VnpResponseCode,
    string? ReceiptUrl,
    string? CreatedByName,
    string? VerifiedByName,
    DateTimeOffset? VerifiedAt,
    string? RejectedReason,
    string? Note,
    DateTimeOffset CreatedAt
);

public sealed record ContractInstallmentDto(
    Guid Id,
    int InstallmentNumber,
    int TotalInstallments,
    string Description,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    DateOnly? DueDate,
    decimal Amount,
    decimal PaidAmount,
    decimal RemainingAmount,
    // UNPAID | PARTIALLY_PAID | PAID | WAIVED | CANCELLED
    string Status,
    DateTimeOffset? PaidAt,
    string? Note,
    bool HasPendingCash
);

public sealed record ContractPenaltyDto(
    Guid Id,
    // RENTAL_CHARGE | INCIDENT_CHARGE
    string SourceKind,
    Guid? RentalChargeId,
    Guid? IncidentChargeId,
    string ChargeType,
    string Description,
    string? IncidentCode,
    string? SerialNumber,
    decimal Amount,
    decimal PaidAmount,
    decimal RemainingAmount,
    string Status,
    bool IsOffsetByDeposit,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    string? Note,
    bool HasPendingCash
);

public sealed record ContractBillingSummaryResponse(
    Guid RentalContractId,
    string ContractCode,
    string RentalType,
    DateOnly StartDate,
    DateOnly EndDate,
    int RentalDays,
    decimal EquipmentRentalTotal,
    decimal ServicesTotal,
    decimal ContractTotalAmount,
    decimal DepositRequiredAmount,
    decimal DepositPaidAmount,
    decimal DepositAppliedAmount,
    decimal DepositRefundedAmount,
    decimal DepositRemainingAmount,
    bool HasPendingCashDeposit,
    decimal InstallmentsTotal,
    decimal InstallmentsPaid,
    decimal InstallmentsRemaining,
    decimal PenaltiesTotal,
    decimal PenaltiesPaid,
    decimal PenaltiesRemaining,
    decimal TotalOutstandingAmount,
    List<ContractInstallmentDto> Installments,
    List<ContractPenaltyDto> Penalties,
    List<PaymentTransactionResponse> Transactions
);
