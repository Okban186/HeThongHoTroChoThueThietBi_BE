namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>
/// Quy định thời gian thuê và cách tính giá thuê thiết bị.
/// - Thuê tối thiểu <see cref="MinRentalDays"/> ngày (tính cả ngày đầu và ngày cuối).
/// - Đến <see cref="ShortTermMaxDays"/> ngày: thuê ngắn hạn, tính theo giá ngày × số ngày.
/// - Trên <see cref="ShortTermMaxDays"/> ngày: thuê dài hạn, tính theo giá tháng × số tháng làm tròn lên (30 ngày/tháng).
/// Đơn giá lấy theo giá niêm yết của model, Sales không được sửa.
/// </summary>
public static class RentalPricing
{
    public const int MinRentalDays = 3;
    public const int ShortTermMaxDays = 10;
    private const int DaysPerMonth = 30;

    public const string ShortTerm = "SHORT_TERM";
    public const string LongTerm = "LONG_TERM";
    public const string Daily = "DAILY";
    public const string Monthly = "MONTHLY";

    /// <summary>Số ngày thuê tính cả ngày đầu và ngày cuối.</summary>
    public static int RentalDays(DateOnly start, DateOnly end) => end.DayNumber - start.DayNumber + 1;

    public static bool IsLongTerm(int days) => days > ShortTermMaxDays;

    public static string RentalTypeFor(int days) => IsLongTerm(days) ? LongTerm : ShortTerm;

    public static string BillingUnitFor(int days) => IsLongTerm(days) ? Monthly : Daily;

    /// <summary>Đơn giá niêm yết theo đơn vị tính tương ứng với số ngày thuê.</summary>
    public static decimal UnitPriceFor(int days, decimal dailyPrice, decimal monthlyPrice)
        => IsLongTerm(days) ? monthlyPrice : dailyPrice;

    /// <summary>DAILY: số ngày; MONTHLY: số tháng làm tròn lên (30 ngày/tháng).</summary>
    public static int BillableUnits(string billingUnit, int days)
        => billingUnit == Monthly ? (int)Math.Ceiling(days / (decimal)DaysPerMonth) : days;
}
