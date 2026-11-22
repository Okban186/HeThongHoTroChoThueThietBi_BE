namespace HeThongHoTroChoThueThietBi.Features.Rental.Configurations;

/// <summary>Cấu hình phiếu giữ chỗ (section "Reservation" trong appsettings).</summary>
public sealed class ReservationOptions
{
    public const string SectionName = "Reservation";

    /// <summary>
    /// Số ngày giữ chỗ kể từ lúc tạo phiếu.
    /// Hạn giữ chỗ = min(thời điểm tạo + HoldDays, hết ngày bắt đầu thuê).
    /// </summary>
    public int HoldDays { get; set; } = 3;
}
