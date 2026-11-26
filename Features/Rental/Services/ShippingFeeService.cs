using System.Globalization;
using System.Text.Json;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Features.Rental.Configurations;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>
/// Tự động đo khoảng cách từ Kho chính đến địa chỉ giao hàng và tính phí vận chuyển
/// theo km (2 lượt: giao đi + thu hồi về) kèm chính sách ưu đãi thuê nhiều.
///
/// Không cần Google Maps API: dùng OpenStreetMap Nominatim (geocoding) + OSRM (đo lộ trình đường bộ),
/// có fallback ước lượng đường chim bay (Haversine × hệ số đường bộ) khi OSRM chậm/lỗi.
/// </summary>
public sealed class ShippingFeeService(
    HttpClient httpClient,
    AppDbContext db,
    IOptions<ShippingOptions> shippingOptions,
    ILogger<ShippingFeeService> logger)
{
    public const string RentalCompanyProvider = "RENTAL_COMPANY";
    public const string CustomerProvider = "CUSTOMER";

    private readonly ShippingOptions _options = shippingOptions.Value;

    public async Task<ShippingQuoteResponse> CalculateAsync(
        string providedBy,
        string? deliveryAddress,
        decimal itemsTotal,
        int totalQuantity,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = providedBy.Trim().ToUpperInvariant();
        if (normalizedProvider != RentalCompanyProvider && normalizedProvider != CustomerProvider)
        {
            throw new AppException(
                "INVALID_DELIVERY_PROVIDER",
                "Phương thức vận chuyển không hợp lệ (chọn Bên cho thuê hoặc Khách tự vận chuyển).",
                StatusCodes.Status400BadRequest,
                "deliveryProvidedBy");
        }

        var origin = await ResolveMainWarehouseOriginAsync(cancellationToken);

        if (normalizedProvider == CustomerProvider)
        {
            return new ShippingQuoteResponse(
                CustomerProvider,
                origin.Address,
                string.IsNullOrWhiteSpace(deliveryAddress) ? null : deliveryAddress.Trim(),
                DistanceKm: null,
                IsEstimated: false,
                FreeRadiusKm: 0,
                ChargeableKm: 0,
                PricePerKmOneWay: _options.PricePerKmOneWay,
                Trips: 0,
                RawFee: 0,
                DiscountPercent: 0,
                DiscountAmount: 0,
                FinalFee: 0,
                PromotionDescription: null,
                SummaryNote: "Khách hàng tự vận chuyển (0 đ)");
        }

        var destination = deliveryAddress?.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new AppException(
                "DELIVERY_ADDRESS_REQUIRED",
                "Vui lòng nhập địa chỉ giao hàng để hệ thống tính phí vận chuyển.",
                StatusCodes.Status400BadRequest,
                "deliveryAddress");
        }

        var destinationCoords = await GeocodeAddressAsync(destination, cancellationToken)
            ?? throw new AppException(
                "DELIVERY_ADDRESS_NOT_FOUND",
                "Không xác định được tọa độ địa chỉ giao hàng. Vui lòng ghi rõ tên đường, phường/xã, quận/huyện, tỉnh/thành phố.",
                StatusCodes.Status400BadRequest,
                "deliveryAddress");

        var (distanceKm, isEstimated) = await MeasureRouteDistanceKmAsync(
            origin.Lat, origin.Lng,
            destinationCoords.Lat, destinationCoords.Lng,
            cancellationToken);

        distanceKm = Math.Round(distanceKm, 1, MidpointRounding.AwayFromZero);

        var (freeRadiusKm, discountPercent, promotionDescription) = ResolvePromotion(itemsTotal, totalQuantity);
        var chargeableKm = Math.Max(0m, distanceKm - freeRadiusKm);
        var trips = Math.Max(1, _options.RoundTripMultiplier);
        var rawFee = RoundToThousand(chargeableKm * _options.PricePerKmOneWay * trips);
        var discountAmount = RoundToThousand(rawFee * (discountPercent / 100m));
        var finalFee = Math.Max(0m, rawFee - discountAmount);

        var noteParts = new List<string>
        {
            $"{distanceKm:0.#} km × {trips} lượt{(isEstimated ? " (ước tính)" : "")}",
            $"Miễn phí {freeRadiusKm:0.#} km đầu"
        };

        if (finalFee == 0)
        {
            noteParts.Add("Miễn phí vận chuyển");
        }
        else if (discountPercent > 0)
        {
            noteParts.Add($"Giảm {discountPercent:0.#}% ưu đãi thuê nhiều");
        }

        return new ShippingQuoteResponse(
            RentalCompanyProvider,
            origin.Address,
            destination,
            distanceKm,
            isEstimated,
            freeRadiusKm,
            chargeableKm,
            _options.PricePerKmOneWay,
            trips,
            rawFee,
            discountPercent,
            discountAmount,
            finalFee,
            promotionDescription,
            string.Join(" · ", noteParts));
    }

    public string BuildPolicySummary()
    {
        return $"Miễn phí trong bán kính {_options.BaseFreeRadiusKm:0.#} km đầu; vượt ngưỡng {_options.PricePerKmOneWay:N0} đ/km × {_options.RoundTripMultiplier} lượt (giao & thu hồi). " +
               $"Ưu đãi thuê nhiều: từ {_options.Tier1MinQuantity} máy hoặc {_options.Tier1MinItemsTotal:N0} đ miễn phí {_options.Tier1FreeRadiusKm:0.#} km & giảm {_options.Tier1DiscountPercent:0.#}%; " +
               $"từ {_options.Tier2MinQuantity} máy hoặc {_options.Tier2MinItemsTotal:N0} đ miễn phí {_options.Tier2FreeRadiusKm:0.#} km & giảm {_options.Tier2DiscountPercent:0.#}%; " +
               $"từ {_options.Tier3MinQuantity} máy hoặc {_options.Tier3MinItemsTotal:N0} đ miễn phí đến {_options.Tier3FreeRadiusKm:0.#} km & giảm {_options.Tier3DiscountPercent:0.#}%.";
    }

    private (decimal FreeRadiusKm, decimal DiscountPercent, string? Description) ResolvePromotion(
        decimal itemsTotal,
        int totalQuantity)
    {
        if (itemsTotal >= _options.Tier3MinItemsTotal || totalQuantity >= _options.Tier3MinQuantity)
        {
            return (
                _options.Tier3FreeRadiusKm,
                _options.Tier3DiscountPercent,
                $"Ưu đãi đơn lớn (≥ {_options.Tier3MinQuantity} máy hoặc ≥ {_options.Tier3MinItemsTotal:N0} đ): miễn phí {_options.Tier3FreeRadiusKm:0.#} km đầu, giảm {_options.Tier3DiscountPercent:0.#}% phí vượt ngưỡng");
        }

        if (itemsTotal >= _options.Tier2MinItemsTotal || totalQuantity >= _options.Tier2MinQuantity)
        {
            return (
                _options.Tier2FreeRadiusKm,
                _options.Tier2DiscountPercent,
                $"Ưu đãi thuê nhiều bậc 2 (≥ {_options.Tier2MinQuantity} máy hoặc ≥ {_options.Tier2MinItemsTotal:N0} đ): miễn phí {_options.Tier2FreeRadiusKm:0.#} km đầu, giảm {_options.Tier2DiscountPercent:0.#}% phí vượt ngưỡng");
        }

        if (itemsTotal >= _options.Tier1MinItemsTotal || totalQuantity >= _options.Tier1MinQuantity)
        {
            return (
                _options.Tier1FreeRadiusKm,
                _options.Tier1DiscountPercent,
                $"Ưu đãi thuê nhiều bậc 1 (≥ {_options.Tier1MinQuantity} máy hoặc ≥ {_options.Tier1MinItemsTotal:N0} đ): miễn phí {_options.Tier1FreeRadiusKm:0.#} km đầu, giảm {_options.Tier1DiscountPercent:0.#}% phí vượt ngưỡng");
        }

        return (_options.BaseFreeRadiusKm, 0m, null);
    }

    private async Task<(string Address, double Lat, double Lng)> ResolveMainWarehouseOriginAsync(
        CancellationToken cancellationToken)
    {
        // Ưu tiên lấy địa chỉ kho hoạt động đầu tiên trong DB nếu có; tọa độ mặc định lấy từ cấu hình Shipping.
        var mainWarehouse = await db.Warehouses
            .AsNoTracking()
            .Where(w => w.Status == Equipment.Enums.WarehouseStatus.Active)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Name, w.Address })
            .FirstOrDefaultAsync(cancellationToken);

        var originAddress = !string.IsNullOrWhiteSpace(mainWarehouse?.Address)
            ? mainWarehouse.Address.Trim()
            : _options.MainWarehouseAddress;

        if (!string.IsNullOrWhiteSpace(mainWarehouse?.Address))
        {
            var coords = await GeocodeAddressAsync(mainWarehouse.Address, cancellationToken);
            if (coords is not null)
                return (originAddress, coords.Value.Lat, coords.Value.Lng);
        }

        return (originAddress, _options.MainWarehouseLat, _options.MainWarehouseLng);
    }

    /// <summary>
    /// Tìm tọa độ qua OpenStreetMap Nominatim. Nếu địa chỉ quá chi tiết (số nhà/hẻm),
    /// tự động rút gọn dần các phân đoạn đầu (sau dấu phẩy) để tăng tỷ lệ tìm thấy tại Việt Nam.
    /// </summary>
    private async Task<(double Lat, double Lng)?> GeocodeAddressAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var candidates = BuildAddressCandidates(address);

        foreach (var candidate in candidates)
        {
            var query = Uri.EscapeDataString(candidate);
            var url = $"https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=vn&q={query}";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("EquipRentApp/1.0 (equipment-rental-shipping)");
                request.Headers.AcceptLanguage.ParseAdd("vi,en;q=0.8");

                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    if (first.TryGetProperty("lat", out var latProp) &&
                        first.TryGetProperty("lon", out var lonProp) &&
                        double.TryParse(latProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                        double.TryParse(lonProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    {
                        return (lat, lon);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning(ex, "Nominatim geocoding failed for candidate '{Candidate}'", candidate);
            }
        }

        return null;
    }

    private static List<string> BuildAddressCandidates(string address)
    {
        var cleaned = address.Trim();
        var results = new List<string> { cleaned };

        var parts = cleaned
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Rút gọn dần phần số nhà/ngõ hẻm đầu tiên (tối đa thử 3 biến thể để tránh spam API).
        if (parts.Count >= 2)
        {
            var withoutFirst = string.Join(", ", parts.Skip(1));
            if (!results.Contains(withoutFirst))
                results.Add(withoutFirst);
        }

        if (parts.Count >= 3)
        {
            var lastTwo = string.Join(", ", parts.Skip(parts.Count - 2));
            if (!results.Contains(lastTwo))
                results.Add(lastTwo);
        }

        return results;
    }

    /// <summary>Đo khoảng cách đường bộ bằng OSRM; fallback Haversine × hệ số đường bộ nếu OSRM lỗi.</summary>
    private async Task<(decimal DistanceKm, bool IsEstimated)> MeasureRouteDistanceKmAsync(
        double originLat,
        double originLng,
        double destLat,
        double destLng,
        CancellationToken cancellationToken)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://router.project-osrm.org/route/v1/driving/{originLng},{originLat};{destLng},{destLat}?overview=false");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("EquipRentApp/1.0 (equipment-rental-shipping)");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (doc.RootElement.TryGetProperty("routes", out var routes) &&
                    routes.ValueKind == JsonValueKind.Array &&
                    routes.GetArrayLength() > 0 &&
                    routes[0].TryGetProperty("distance", out var distanceMetersProp) &&
                    distanceMetersProp.TryGetDouble(out var distanceMeters) &&
                    distanceMeters >= 0)
                {
                    return ((decimal)(distanceMeters / 1000.0), false);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "OSRM routing unavailable; falling back to Haversine estimate.");
        }

        var straightKm = HaversineKm(originLat, originLng, destLat, destLng);
        return ((decimal)(straightKm * _options.RoadDetourFactor), true);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusKm = 6371.0;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double DegreesToRadians(double deg) => deg * (Math.PI / 180.0);

    private static decimal RoundToThousand(decimal value) =>
        Math.Round(value / 1000m, MidpointRounding.AwayFromZero) * 1000m;
}
