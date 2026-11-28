using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using HeThongHoTroChoThueThietBi.Common.Exceptions;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Services;

public sealed record RouteWaypoint(
    Guid ReferenceId,
    string Label,
    string Address,
    double? Lat,
    double? Lng,
    int InstallMinutes);

public sealed record OptimizedStopResult(
    Guid ReferenceId,
    string Label,
    string Address,
    double Latitude,
    double Longitude,
    int StopOrder,
    int TravelMinutesFromPrevious,
    double DistanceKmFromPrevious,
    int InstallMinutes);

public sealed record OptimizedTripResponse(
    string Provider, // "GOOGLE" | "OSRM" | "FALLBACK_ESTIMATE"
    List<OptimizedStopResult> Stops,
    int ReturnTravelMinutes,
    double ReturnDistanceKm,
    int TotalTravelMinutes,
    int TotalInstallMinutes,
    int TotalDurationMinutes);

public sealed class RouteOptimizationService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<RouteOptimizationService> logger)
{
    private const double SameLocationThresholdMeters = 50.0;
    private const double SameLocationThresholdKm = 0.05;

    public async Task<(double Lat, double Lng)> ResolveAddressAsync(
        string address, string labelForError, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new AppException(
                "ADDRESS_REQUIRED",
                $"Thiếu địa chỉ giao hàng cho {labelForError}.",
                StatusCodes.Status400BadRequest);
        }

        // 1. Ưu tiên Google Geocoding API
        var apiKey = configuration["GoogleMaps:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={Uri.EscapeDataString(address)}&region=vn&key={apiKey}";
                using var res = await httpClient.GetAsync(url, ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    var results = doc.RootElement.GetProperty("results");
                    if (results.GetArrayLength() > 0)
                    {
                        var loc = results[0].GetProperty("geometry").GetProperty("location");
                        return (loc.GetProperty("lat").GetDouble(), loc.GetProperty("lng").GetDouble());
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Google Geocoding lỗi cho địa chỉ '{Address}', chuyển sang Nominatim.", address);
            }
        }

        // 2. Fallback Nominatim (OpenStreetMap - miễn phí)
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://nominatim.openstreetmap.org/search?format=json&limit=1&countrycodes=vn&q={Uri.EscapeDataString(address)}");
            req.Headers.UserAgent.ParseAdd("EquipRentApp/1.0");
            using var res = await httpClient.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    return (
                        double.Parse(first.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture),
                        double.Parse(first.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nominatim Geocoding lỗi cho địa chỉ '{Address}'.", address);
        }

        throw new AppException(
            "GEOCODING_FAILED",
            $"Không thể xác định tọa độ bản đồ cho địa chỉ \"{address}\" ({labelForError}). Vui lòng cập nhật địa chỉ đầy đủ hơn (số nhà, đường, quận/huyện, thành phố).",
            StatusCodes.Status400BadRequest);
    }

    public async Task<OptimizedTripResponse> OptimizeRoundTripAsync(
        (double Lat, double Lng, string Address) depot,
        List<RouteWaypoint> stops,
        CancellationToken ct = default)
    {
        var resolved = new List<(RouteWaypoint Stop, double Lat, double Lng)>(stops.Count);
        foreach (var s in stops)
        {
            if (s.Lat.HasValue && s.Lng.HasValue)
            {
                resolved.Add((s, s.Lat.Value, s.Lng.Value));
            }
            else
            {
                var (lat, lng) = await ResolveAddressAsync(s.Address, s.Label, ct);
                resolved.Add((s, lat, lng));
            }
        }

        // 1. Thử Google Routes API v2 trước
        var apiKey = configuration["GoogleMaps:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                var googleResult = await TryGoogleRoutesAsync(apiKey, depot, resolved, ct);
                if (googleResult is not null) return googleResult;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Google Routes API v2 thất bại, chuyển sang OSRM.");
            }
        }

        // 2. Fallback OSRM Trip API
        try
        {
            var osrmResult = await TryOsrmTripAsync(depot, resolved, ct);
            if (osrmResult is not null) return osrmResult;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "OSRM Trip API thất bại, dùng ước lượng Haversine.");
        }

        // 3. Fallback cuối cùng (Nearest-Neighbor + Haversine)
        return BuildHaversineFallback(depot, resolved);
    }

    private async Task<OptimizedTripResponse?> TryGoogleRoutesAsync(
        string apiKey,
        (double Lat, double Lng, string Address) depot,
        List<(RouteWaypoint Stop, double Lat, double Lng)> points,
        CancellationToken ct)
    {
        var payload = new
        {
            origin = new { location = new { latLng = new { latitude = depot.Lat, longitude = depot.Lng } } },
            destination = new { location = new { latLng = new { latitude = depot.Lat, longitude = depot.Lng } } },
            intermediates = points.Select(p => new
            {
                location = new { latLng = new { latitude = p.Lat, longitude = p.Lng } }
            }).ToArray(),
            travelMode = "DRIVE",
            routingPreference = "TRAFFIC_AWARE",
            optimizeWaypointOrder = points.Count > 1
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes");
        req.Headers.Add("X-Goog-Api-Key", apiKey);
        req.Headers.Add("X-Goog-FieldMask", "routes.legs.duration,routes.legs.distanceMeters,routes.optimizedIntermediateWaypointIndex");
        req.Content = JsonContent.Create(payload);

        using var res = await httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
            return null;

        var route = routes[0];
        var legs = route.GetProperty("legs");

        var order = points.Count > 1 && route.TryGetProperty("optimizedIntermediateWaypointIndex", out var optIdx)
            ? optIdx.EnumerateArray().Select(x => x.GetInt32()).ToList()
            : Enumerable.Range(0, points.Count).ToList();

        var stopResults = new List<OptimizedStopResult>();
        var totalTravel = 0;
        double prevLat = depot.Lat, prevLng = depot.Lng;
        string prevAddress = depot.Address;

        for (var i = 0; i < order.Count; i++)
        {
            var p = points[order[i]];
            var leg = legs[i];
            var distMeters = leg.TryGetProperty("distanceMeters", out var dm) ? dm.GetDouble() : 0.0;
            var isSamePlace = IsSameLocation(prevLat, prevLng, prevAddress, p.Lat, p.Lng, p.Stop.Address, distMeters);

            var km = isSamePlace ? 0.0 : Math.Round(distMeters / 1000.0, 1);
            var mins = isSamePlace ? 0 : ParseGoogleDurationMinutes(leg.GetProperty("duration").GetString());
            totalTravel += mins;

            stopResults.Add(new OptimizedStopResult(
                p.Stop.ReferenceId, p.Stop.Label, p.Stop.Address,
                p.Lat, p.Lng, i + 1, mins, km, p.Stop.InstallMinutes));

            prevLat = p.Lat;
            prevLng = p.Lng;
            prevAddress = p.Stop.Address;
        }

        var returnLeg = legs[order.Count];
        var returnDistMeters = returnLeg.TryGetProperty("distanceMeters", out var rdm) ? rdm.GetDouble() : 0.0;
        var returnSamePlace = IsSameLocation(prevLat, prevLng, prevAddress, depot.Lat, depot.Lng, depot.Address, returnDistMeters);
        var returnKm = returnSamePlace ? 0.0 : Math.Round(returnDistMeters / 1000.0, 1);
        var returnMins = returnSamePlace ? 0 : ParseGoogleDurationMinutes(returnLeg.GetProperty("duration").GetString());
        totalTravel += returnMins;

        var totalInstall = stopResults.Sum(s => s.InstallMinutes);
        return new OptimizedTripResponse("GOOGLE", stopResults, returnMins, returnKm, totalTravel, totalInstall, totalTravel + totalInstall);
    }

    private async Task<OptimizedTripResponse?> TryOsrmTripAsync(
        (double Lat, double Lng, string Address) depot,
        List<(RouteWaypoint Stop, double Lat, double Lng)> points,
        CancellationToken ct)
    {
        var coords = new List<string>
        {
            $"{depot.Lng.ToString(CultureInfo.InvariantCulture)},{depot.Lat.ToString(CultureInfo.InvariantCulture)}"
        };
        coords.AddRange(points.Select(p =>
            $"{p.Lng.ToString(CultureInfo.InvariantCulture)},{p.Lat.ToString(CultureInfo.InvariantCulture)}"));

        var url = $"https://router.project-osrm.org/trip/v1/driving/{string.Join(";", coords)}?source=first&roundtrip=true";
        using var res = await httpClient.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (doc.RootElement.GetProperty("code").GetString() != "Ok") return null;

        var trip = doc.RootElement.GetProperty("trips")[0];
        var legs = trip.GetProperty("legs");
        var waypoints = doc.RootElement.GetProperty("waypoints");

        var orderedInputIndices = new int[points.Count];
        for (var i = 1; i <= points.Count; i++)
        {
            var wpOrder = waypoints[i].GetProperty("waypoint_index").GetInt32();
            if (wpOrder >= 1 && wpOrder <= points.Count)
                orderedInputIndices[wpOrder - 1] = i - 1;
        }

        var stopResults = new List<OptimizedStopResult>();
        var totalTravel = 0;
        double prevLat = depot.Lat, prevLng = depot.Lng;
        string prevAddress = depot.Address;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[orderedInputIndices[i]];
            var leg = legs[i];
            var distMeters = leg.GetProperty("distance").GetDouble();
            var isSamePlace = IsSameLocation(prevLat, prevLng, prevAddress, p.Lat, p.Lng, p.Stop.Address, distMeters);

            var km = isSamePlace ? 0.0 : Math.Round(distMeters / 1000.0, 1);
            var mins = isSamePlace ? 0 : Math.Max(5, (int)Math.Ceiling(leg.GetProperty("duration").GetDouble() / 60.0));
            totalTravel += mins;

            stopResults.Add(new OptimizedStopResult(
                p.Stop.ReferenceId, p.Stop.Label, p.Stop.Address,
                p.Lat, p.Lng, i + 1, mins, km, p.Stop.InstallMinutes));

            prevLat = p.Lat;
            prevLng = p.Lng;
            prevAddress = p.Stop.Address;
        }

        var returnLeg = legs[points.Count];
        var returnDistMeters = returnLeg.GetProperty("distance").GetDouble();
        var returnSamePlace = IsSameLocation(prevLat, prevLng, prevAddress, depot.Lat, depot.Lng, depot.Address, returnDistMeters);
        var returnKm = returnSamePlace ? 0.0 : Math.Round(returnDistMeters / 1000.0, 1);
        var returnMins = returnSamePlace ? 0 : Math.Max(5, (int)Math.Ceiling(returnLeg.GetProperty("duration").GetDouble() / 60.0));
        totalTravel += returnMins;

        var totalInstall = stopResults.Sum(s => s.InstallMinutes);
        return new OptimizedTripResponse("OSRM", stopResults, returnMins, returnKm, totalTravel, totalInstall, totalTravel + totalInstall);
    }

    private static int ParseGoogleDurationMinutes(string? durationStr)
    {
        if (string.IsNullOrWhiteSpace(durationStr)) return 20;
        var clean = durationStr.TrimEnd('s');
        if (!double.TryParse(clean, CultureInfo.InvariantCulture, out var sec))
            return 20;
        return sec <= 0 ? 0 : Math.Max(5, (int)Math.Ceiling(sec / 60.0));
    }

    private static OptimizedTripResponse BuildHaversineFallback(
        (double Lat, double Lng, string Address) depot,
        List<(RouteWaypoint Stop, double Lat, double Lng)> points)
    {
        // Sắp xếp theo điểm gần nhất (Nearest-Neighbor) để các điểm chung địa chỉ luôn nằm liền kề nhau
        var remaining = new List<(RouteWaypoint Stop, double Lat, double Lng)>(points);
        var results = new List<OptimizedStopResult>(points.Count);
        double curLat = depot.Lat, curLng = depot.Lng;
        string curAddress = depot.Address;
        var totalTravel = 0;
        var order = 1;

        while (remaining.Count > 0)
        {
            var bestIdx = 0;
            var bestDist = HaversineKm(curLat, curLng, remaining[0].Lat, remaining[0].Lng);
            for (var i = 1; i < remaining.Count; i++)
            {
                var d = HaversineKm(curLat, curLng, remaining[i].Lat, remaining[i].Lng);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            var next = remaining[bestIdx];
            remaining.RemoveAt(bestIdx);

            var isSamePlace = IsSameLocation(curLat, curLng, curAddress, next.Lat, next.Lng, next.Stop.Address, bestDist * 1000.0);
            var km = isSamePlace ? 0.0 : Math.Round(bestDist * 1.3, 1);
            var mins = isSamePlace ? 0 : Math.Max(10, (int)Math.Ceiling(km / 25.0 * 60));
            totalTravel += mins;

            results.Add(new OptimizedStopResult(
                next.Stop.ReferenceId, next.Stop.Label, next.Stop.Address,
                next.Lat, next.Lng, order++, mins, km, next.Stop.InstallMinutes));

            curLat = next.Lat;
            curLng = next.Lng;
            curAddress = next.Stop.Address;
        }

        var retRawKm = HaversineKm(curLat, curLng, depot.Lat, depot.Lng);
        var retSamePlace = IsSameLocation(curLat, curLng, curAddress, depot.Lat, depot.Lng, depot.Address, retRawKm * 1000.0);
        var retKm = retSamePlace ? 0.0 : Math.Round(retRawKm * 1.3, 1);
        var retMins = retSamePlace ? 0 : Math.Max(10, (int)Math.Ceiling(retKm / 25.0 * 60));
        totalTravel += retMins;

        var totalInstall = results.Sum(x => x.InstallMinutes);
        return new OptimizedTripResponse("FALLBACK_ESTIMATE", results, retMins, retKm, totalTravel, totalInstall, totalTravel + totalInstall);
    }

    private static bool IsSameLocation(
        double lat1, double lng1, string? addr1,
        double lat2, double lng2, string? addr2,
        double reportedDistanceMeters)
    {
        if (reportedDistanceMeters < SameLocationThresholdMeters)
            return true;

        if (HaversineKm(lat1, lng1, lat2, lng2) < SameLocationThresholdKm)
            return true;

        if (!string.IsNullOrWhiteSpace(addr1) && !string.IsNullOrWhiteSpace(addr2) &&
            string.Equals(addr1.Trim(), addr2.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
