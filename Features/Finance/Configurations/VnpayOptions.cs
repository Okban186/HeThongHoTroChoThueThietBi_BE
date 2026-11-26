namespace HeThongHoTroChoThueThietBi.Features.Finance.Configurations;

public sealed class VnpayOptions
{
    public const string SectionName = "Vnpay";

    public string TmnCode { get; init; } = string.Empty;
    public string HashSecret { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
    public string Version { get; init; } = "2.1.0";
    public string Command { get; init; } = "pay";
    public string CurrCode { get; init; } = "VND";
    public string Locale { get; init; } = "vn";

    /// <summary>
    /// Public URL (HTTPS qua cloudflared/ngrok hoặc domain thật) để VNPAY chuyển hướng sau thanh toán.
    /// Nếu để trống sẽ tự ghép theo Host của request tạo giao dịch.
    /// </summary>
    public string ReturnUrl { get; init; } = string.Empty;

    /// <summary>Thời hạn hiệu lực của mỗi link/mã QR VNPAY (mặc định 15 phút).</summary>
    public int ExpireMinutes { get; init; } = 15;
}
