using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>Yêu cầu báo giá phí vận chuyển tự động cho form tạo/sửa hợp đồng.</summary>
public sealed class ShippingQuoteRequest
{
    public Guid? ReservationId { get; init; }
    public Guid? ContractId { get; init; }

    /// <summary>RENTAL_COMPANY (bên cho thuê giao &amp; thu hồi) hoặc CUSTOMER (khách tự vận chuyển).</summary>
    [Required]
    public string ProvidedBy { get; init; } = "RENTAL_COMPANY";

    [MaxLength(500)]
    public string? DeliveryAddress { get; init; }
}

/// <summary>Kết quả tính phí vận chuyển tự động từ Backend (Sales chỉ xem, không chỉnh sửa).</summary>
public sealed record ShippingQuoteResponse(
    // RENTAL_COMPANY hoặc CUSTOMER
    string ProvidedBy,
    string OriginAddress,
    string? DestinationAddress,
    // Khoảng cách 1 chiều (km); null khi khách tự vận chuyển.
    decimal? DistanceKm,
    // True nếu OSRM không khả dụng và hệ thống ước lượng theo đường chim bay × hệ số đường bộ.
    bool IsEstimated,
    decimal FreeRadiusKm,
    decimal ChargeableKm,
    decimal PricePerKmOneWay,
    int Trips,
    decimal RawFee,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal FinalFee,
    string? PromotionDescription,
    string SummaryNote
);
