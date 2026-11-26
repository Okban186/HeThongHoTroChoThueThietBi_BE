namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>Hợp đồng thuê (danh sách).</summary>
public sealed record RentalContractResponse(
    Guid Id,
    string ContractCode,
    Guid? ReservationId,
    string? ReservationCode,
    Guid CompanyId,
    string CompanyCode,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    // SHORT_TERM, LONG_TERM
    string RentalType,
    // DRAFT, PENDING_APPROVAL, REJECTED, PENDING_SIGNATURE, SIGNED, ...
    string Status,
    decimal ContractTotalAmount,
    decimal DepositRequiredAmount,
    decimal DepositPaidAmount,
    int ItemCount,
    int TotalQuantity,
    Guid CreatedById,
    string? CreatedByName,
    // Người đang đăng nhập là người tạo hợp đồng.
    bool CanEdit,
    string? RejectionReason,
    DateTimeOffset CreatedAt
);
