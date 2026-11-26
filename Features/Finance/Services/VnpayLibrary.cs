using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HeThongHoTroChoThueThietBi.Features.Finance.Configurations;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Services;

/// <summary>
/// Tiện ích ký và xác thực chữ ký HMAC-SHA512 theo chuẩn VNPAY 2.1.0.
/// Toàn bộ timestamp (vnp_CreateDate, vnp_ExpireDate) tính theo múi giờ Việt Nam (GMT+7).
/// </summary>
public static partial class VnpayLibrary
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static string BuildPaymentUrl(
        VnpayOptions options,
        string txnRef,
        decimal amountVnd,
        string orderInfo,
        string clientIp,
        string returnUrl,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc)
    {
        var createGmt7 = createdUtc.ToOffset(VietnamOffset);
        var expireGmt7 = expiresUtc.ToOffset(VietnamOffset);

        var data = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Version"] = options.Version,
            ["vnp_Command"] = options.Command,
            ["vnp_TmnCode"] = options.TmnCode.Trim(),
            ["vnp_Amount"] = ((long)Math.Round(amountVnd * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
            ["vnp_CreateDate"] = createGmt7.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            ["vnp_CurrCode"] = options.CurrCode,
            ["vnp_IpAddr"] = NormalizeIp(clientIp),
            ["vnp_Locale"] = options.Locale,
            ["vnp_OrderInfo"] = SanitizeOrderInfo(orderInfo),
            ["vnp_OrderType"] = "other",
            ["vnp_ReturnUrl"] = returnUrl,
            ["vnp_ExpireDate"] = expireGmt7.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            ["vnp_TxnRef"] = txnRef,
        };

        var queryBuilder = new StringBuilder();
        foreach (var (key, value) in data)
        {
            if (string.IsNullOrEmpty(value))
                continue;

            queryBuilder.Append(WebUtility.UrlEncode(key));
            queryBuilder.Append('=');
            queryBuilder.Append(WebUtility.UrlEncode(value));
            queryBuilder.Append('&');
        }

        var rawSignData = queryBuilder.ToString().TrimEnd('&');
        var secureHash = ComputeHmacSha512(options.HashSecret.Trim(), rawSignData);

        return $"{options.BaseUrl}?{rawSignData}&vnp_SecureHash={secureHash}";
    }

    public static bool ValidateSignature(IQueryCollection query, string hashSecret)
    {
        var receivedHash = query["vnp_SecureHash"].ToString();
        if (string.IsNullOrWhiteSpace(receivedHash) || string.IsNullOrWhiteSpace(hashSecret))
            return false;

        var data = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query)
        {
            if (!pair.Key.StartsWith("vnp_", StringComparison.Ordinal))
                continue;

            if (pair.Key == "vnp_SecureHash" || pair.Key == "vnp_SecureHashType")
                continue;

            var value = pair.Value.ToString();
            if (!string.IsNullOrEmpty(value))
                data[pair.Key] = value;
        }

        var signBuilder = new StringBuilder();
        foreach (var (key, value) in data)
        {
            signBuilder.Append(WebUtility.UrlEncode(key));
            signBuilder.Append('=');
            signBuilder.Append(WebUtility.UrlEncode(value));
            signBuilder.Append('&');
        }

        var signData = signBuilder.ToString().TrimEnd('&');
        var expectedHash = ComputeHmacSha512(hashSecret.Trim(), signData);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedHash.ToLowerInvariant()),
            Encoding.UTF8.GetBytes(receivedHash.Trim().ToLowerInvariant()));
    }

    public static string ComputeHmacSha512(string key, string inputData)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var inputBytes = Encoding.UTF8.GetBytes(inputData);
        using var hmac = new HMACSHA512(keyBytes);
        var hashBytes = hmac.ComputeHash(inputBytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>Chuyển tiếng Việt có dấu sang không dấu và loại bỏ ký tự đặc biệt để VNPAY không lỗi checksum.</summary>
    public static string SanitizeOrderInfo(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            sb.Append(c switch
            {
                'đ' => 'd',
                'Đ' => 'D',
                _ => c,
            });
        }

        var ascii = sb.ToString().Normalize(NormalizationForm.FormC);
        ascii = InvalidOrderInfoChars().Replace(ascii, " ");
        ascii = MultiSpace().Replace(ascii, " ").Trim();

        return ascii.Length > 200 ? ascii[..200] : ascii;
    }

    private static string NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "::1")
            return "127.0.0.1";

        var first = ip.Split(',')[0].Trim();
        return first.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)
            ? first[7..]
            : first;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9 \-_./:]")]
    private static partial Regex InvalidOrderInfoChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpace();
}
