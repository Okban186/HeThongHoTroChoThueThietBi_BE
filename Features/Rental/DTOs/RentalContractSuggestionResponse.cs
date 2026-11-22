namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>
/// Giá trị đề xuất khi tạo hợp đồng từ phiếu đã xác nhận.
/// Thuê dưới 30 ngày: SHORT_TERM, tính theo ngày; từ 30 ngày: LONG_TERM, tính theo tháng.
/// Đơn giá lấy từ giá thuê của model; cọc 20% tổng giá trị (làm tròn nghìn đồng).
/// </summary>
public sealed record RentalContractSuggestionResponse(
    Guid ReservationId,
    string ReservationCode,
    Guid CompanyId,
    string CompanyName,
    DateOnly StartDate,
    DateOnly EndDate,
    int RentalDays,
    string RentalType,
    decimal DepositRate,
    List<RentalContractItemResponse> Items,
    decimal ContractTotalAmount,
    decimal DepositRequiredAmount
);
