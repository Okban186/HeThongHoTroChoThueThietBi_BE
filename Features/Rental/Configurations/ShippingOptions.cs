namespace HeThongHoTroChoThueThietBi.Features.Rental.Configurations;

/// <summary>
/// Chính sách tính phí vận chuyển tự động từ Kho chính tới điểm giao hàng.
/// Tính 2 lượt (giao đi + thu hồi về) và áp dụng ưu đãi tự động theo giá trị/số lượng thuê.
/// </summary>
public sealed class ShippingOptions
{
    public const string SectionName = "Shipping";

    public string MainWarehouseName { get; init; } = "Kho chính TP. Hồ Chí Minh";
    public string MainWarehouseAddress { get; init; } = "Quận 1, TP. Hồ Chí Minh";
    public double MainWarehouseLat { get; init; } = 10.7769;
    public double MainWarehouseLng { get; init; } = 106.7009;

    /// <summary>Bán kính miễn phí tiêu chuẩn (km, 1 chiều).</summary>
    public decimal BaseFreeRadiusKm { get; init; } = 5m;

    /// <summary>Đơn giá mỗi km vượt ngưỡng cho 1 chiều (nhân 2 lượt khi tính phí).</summary>
    public decimal PricePerKmOneWay { get; init; } = 15_000m;

    /// <summary>Số lượt vận chuyển (Giao đi + Thu hồi về).</summary>
    public int RoundTripMultiplier { get; init; } = 2;

    /// <summary>Hệ số quy đổi đường chim bay sang lộ trình đường bộ khi OSRM không khả dụng.</summary>
    public double RoadDetourFactor { get; init; } = 1.3;

    // Ưu đãi bậc 1: thuê từ 10 triệu hoặc >= 5 máy -> miễn phí 10 km, giảm 15% phần vượt.
    public decimal Tier1MinItemsTotal { get; init; } = 10_000_000m;
    public int Tier1MinQuantity { get; init; } = 5;
    public decimal Tier1FreeRadiusKm { get; init; } = 10m;
    public decimal Tier1DiscountPercent { get; init; } = 15m;

    // Ưu đãi bậc 2: thuê từ 25 triệu hoặc >= 10 máy -> miễn phí 15 km, giảm 30% phần vượt.
    public decimal Tier2MinItemsTotal { get; init; } = 25_000_000m;
    public int Tier2MinQuantity { get; init; } = 10;
    public decimal Tier2FreeRadiusKm { get; init; } = 15m;
    public decimal Tier2DiscountPercent { get; init; } = 30m;

    // Ưu đãi bậc 3 (đơn lớn): thuê từ 50 triệu hoặc >= 20 máy -> miễn phí 100% trong 50 km, giảm 50% phần vượt.
    public decimal Tier3MinItemsTotal { get; init; } = 50_000_000m;
    public int Tier3MinQuantity { get; init; } = 20;
    public decimal Tier3FreeRadiusKm { get; init; } = 50m;
    public decimal Tier3DiscountPercent { get; init; } = 50m;
}
