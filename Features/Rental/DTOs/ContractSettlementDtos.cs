using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

public sealed record SettlementChargeItemDto(
    Guid Id,
    string SourceKind, // RENTAL_CHARGE | INCIDENT_CHARGE
    string ChargeType,
    string Description,
    decimal Amount,
    decimal PaidAmount,
    decimal RemainingAmount,
    string Status,
    DateOnly? DueDate);

public sealed record ContractSettlementPreviewResponse(
    Guid ContractId,
    string ContractCode,
    string CompanyName,
    string ContractStatus,
    DateOnly StartDate,
    DateOnly EndDate,
    int OutstandingEquipmentCount,
    int PendingReturnReceiptCount,
    decimal DepositRequiredAmount,
    decimal DepositPaidAmount,
    decimal DepositAppliedAmount,
    decimal DepositRefundedAmount,
    decimal AvailableDepositAmount,
    decimal TotalUnpaidChargesAmount,
    decimal ProjectedDepositOffsetAmount,
    decimal ProjectedDepositRefundAmount,
    decimal ProjectedCustomerShortfallAmount,
    bool CanCompleteContract,
    string? BlockingReason,
    List<SettlementChargeItemDto> UnpaidItems);

public sealed class SettleContractRequest
{
    /// <summary>Tự động cấn trừ các khoản nợ/phạt còn thiếu vào tiền cọc khả dụng.</summary>
    public bool ApplyDepositToUnpaidCharges { get; init; } = true;

    /// <summary>Hoàn trả tiền đặt cọc còn dư cho khách hàng sau khi đã trừ hết nợ/phạt.</summary>
    public bool RefundRemainingDeposit { get; init; } = true;

    /// <summary>Số tiền hoàn cọc tùy chỉnh (nếu null sẽ hoàn toàn bộ số cọc dư khả dụng).</summary>
    [Range(0, 999_999_999_999, ErrorMessage = "Số tiền hoàn cọc không hợp lệ.")]
    public decimal? CustomRefundAmount { get; init; }

    [MaxLength(1000)]
    public string? Note { get; init; }
}

public sealed record SettleContractResponse(
    Guid ContractId,
    string ContractCode,
    string ContractStatus,
    decimal NewlyOffsetDepositAmount,
    decimal NewlyRefundedDepositAmount,
    decimal RemainingDepositAmount,
    decimal RemainingCustomerDebtAmount,
    bool IsCompleted,
    string Message);
