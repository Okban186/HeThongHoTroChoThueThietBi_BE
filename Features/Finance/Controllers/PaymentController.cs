using System.Net;
using System.Security.Claims;
using System.Text.Json;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Features.Finance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Controllers;

/// <summary>
/// Cổng thanh toán VNPAY Sandbox (QR + IPN + ReturnUrl) và thu tiền mặt (CASH chờ Manager duyệt).
/// Áp dụng cho đặt cọc, các đợt tiền thuê, khoản phạt và phí sự cố.
/// </summary>
[ApiController]
[Route("api/payments")]
public sealed class PaymentController(PaymentService paymentService) : ControllerBase
{
    /// <summary>Tạo giao dịch VNPAY Sandbox và trả về paymentUrl để Flutter vẽ mã QR (hạn 15 phút).</summary>
    [HttpPost("vnpay/create")]
    [Authorize(Roles = "COMPANY,SALES,MANAGER,TECHNICIAN")]
    public async Task<ActionResult<PaymentTransactionResponse>> CreateVnpayPayment(
        [FromBody] CreateVnpayPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var fallbackReturnUrl = $"{Request.Scheme}://{Request.Host}/api/payments/vnpay-return";
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        var result = await paymentService.CreateVnpayPaymentAsync(
            request,
            CurrentUserId(),
            IsCustomer(),
            CanViewAll(),
            clientIp,
            fallbackReturnUrl,
            isTechnicianOnly: IsTechnicianOnly(),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Kiểm tra trạng thái giao dịch (Flutter polling 3-5 giây/lần khi đang mở màn QR).</summary>
    [HttpGet("{id:guid}/status")]
    [Authorize(Roles = "COMPANY,SALES,MANAGER,TECHNICIAN")]
    public async Task<ActionResult<PaymentTransactionResponse>> GetStatus(
        Guid id,
        CancellationToken cancellationToken)
    {
        return Ok(await paymentService.GetTransactionStatusAsync(
            id, CurrentUserId(), IsCustomer(), CanViewAll(), cancellationToken));
    }

    /// <summary>Khách/Sales hủy mã QR VNPAY đang chờ ngay trên app Flutter.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "COMPANY,SALES,MANAGER,TECHNICIAN")]
    public async Task<ActionResult<PaymentTransactionResponse>> Cancel(
        Guid id,
        CancellationToken cancellationToken)
    {
        return Ok(await paymentService.CancelVnpayTransactionAsync(
            id, CurrentUserId(), IsCustomer(), CanViewAll(), cancellationToken));
    }

    /// <summary>IPN endpoint (Server-to-Server) VNPAY gọi tự động để xác nhận thanh toán và gạch nợ.</summary>
    [AllowAnonymous]
    [HttpGet("vnpay-ipn")]
    public async Task<IActionResult> VnpayIpn(CancellationToken cancellationToken)
    {
        var result = await paymentService.ProcessVnpayCallbackAsync(Request.Query, cancellationToken);
        // VNPAY yêu cầu đúng tên khóa PascalCase: RspCode, Message.
        var json = JsonSerializer.Serialize(new { result.RspCode, result.Message });
        return Content(json, "application/json; charset=utf-8");
    }

    /// <summary>ReturnUrl khi người dùng thanh toán xong trên trình duyệt VNPAY Sandbox.</summary>
    [AllowAnonymous]
    [HttpGet("vnpay-return")]
    public async Task<IActionResult> VnpayReturn(CancellationToken cancellationToken)
    {
        var result = await paymentService.ProcessVnpayCallbackAsync(Request.Query, cancellationToken);

        var (title, badgeColor, subtitle) = result.IsSuccess
            ? ("Thanh toán thành công", "#21865D", "Hệ thống EquipRent đã tự động ghi nhận giao dịch. Bạn có thể đóng trang này và quay lại ứng dụng.")
            : result.IsCancelled
                ? ("Giao dịch đã bị hủy", "#C98218", "Bạn đã hủy thanh toán trên cổng VNPAY. Bạn có thể quay lại ứng dụng để tạo mã thanh toán mới.")
                : ("Thanh toán không thành công", "#D63845", "Giao dịch không hoàn tất hoặc chữ ký không hợp lệ. Vui lòng quay lại ứng dụng và thử lại.");

        var amountText = result.Amount.HasValue ? $"{result.Amount.Value:N0} đ" : "—";
        var html = $$"""
            <!DOCTYPE html>
            <html lang="vi">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{WebUtility.HtmlEncode(title)}} - EquipRent</title>
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #F3F6FF; margin: 0; padding: 24px; color: #142B55; }
                .card { max-width: 440px; margin: 40px auto; background: #fff; border-radius: 16px; padding: 28px; box-shadow: 0 8px 24px rgba(20,43,85,0.08); text-align: center; }
                .badge { display: inline-block; padding: 8px 16px; border-radius: 999px; color: #fff; background: {{badgeColor}}; font-weight: 700; font-size: 14px; }
                h1 { font-size: 22px; margin: 16px 0 8px; }
                p { color: #647796; line-height: 1.5; font-size: 14px; }
                .meta { background: #F7F9FE; border: 1px solid #DCE5F5; border-radius: 12px; padding: 14px; margin-top: 18px; text-align: left; font-size: 13.5px; }
                .row { display: flex; justify-content: space-between; margin: 6px 0; gap: 12px; }
                .lbl { color: #647796; }
                .val { font-weight: 700; text-align: right; }
              </style>
            </head>
            <body>
              <div class="card">
                <span class="badge">EquipRent · VNPAY</span>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p>{{WebUtility.HtmlEncode(subtitle)}}</p>
                <div class="meta">
                  <div class="row"><span class="lbl">Mã giao dịch</span><span class="val">{{WebUtility.HtmlEncode(result.TxnRef ?? "—")}}</span></div>
                  <div class="row"><span class="lbl">Số tiền</span><span class="val">{{WebUtility.HtmlEncode(amountText)}}</span></div>
                  <div class="row"><span class="lbl">Nội dung</span><span class="val">{{WebUtility.HtmlEncode(result.OrderInfo ?? "—")}}</span></div>
                </div>
              </div>
            </body>
            </html>
            """;

        return Content(html, "text/html; charset=utf-8");
    }

    // ================================================================ tiền mặt (CASH)

    [HttpPost("cash/upload-ticket")]
    [Authorize(Roles = "SALES,MANAGER,STOREKEEPER,LEAD_STOREKEEPER,TECHNICIAN")]
    public async Task<ActionResult<FileUploadTicket>> CreateCashReceiptUploadTicket(CancellationToken cancellationToken)
        => Ok(await paymentService.CreateCashReceiptUploadTicketAsync(CurrentUserId(), cancellationToken));

    [HttpPost("cash/confirm-upload")]
    [Authorize(Roles = "SALES,MANAGER,STOREKEEPER,LEAD_STOREKEEPER,TECHNICIAN")]
    public async Task<ActionResult<ConfirmFileUploadResponse>> ConfirmCashReceiptUpload(
        [FromBody] ConfirmFileUploadRequest request,
        CancellationToken cancellationToken)
        => Ok(await paymentService.ConfirmCashReceiptUploadAsync(request, CurrentUserId(), cancellationToken));

    [HttpPost("cash")]
    [Authorize(Roles = "SALES,MANAGER,STOREKEEPER,LEAD_STOREKEEPER,TECHNICIAN")]
    public async Task<ActionResult<PaymentTransactionResponse>> CreateCashPayment(
        [FromBody] CreateCashPaymentRequest request,
        CancellationToken cancellationToken)
        => Ok(await paymentService.CreateCashPaymentAsync(request, CurrentUserId(), CanViewAll(), isTechnicianOnly: IsTechnicianOnly(), cancellationToken));

    [HttpGet("pending-cash")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<List<PaymentTransactionResponse>>> GetPendingCashPayments(CancellationToken cancellationToken)
        => Ok(await paymentService.GetPendingCashPaymentsAsync(CurrentUserId(), cancellationToken));

    [HttpPost("{id:guid}/verify-cash")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<PaymentTransactionResponse>> VerifyCashPayment(Guid id, CancellationToken cancellationToken)
        => Ok(await paymentService.VerifyCashPaymentAsync(id, CurrentUserId(), cancellationToken));

    [HttpPost("{id:guid}/reject-cash")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<PaymentTransactionResponse>> RejectCashPayment(
        Guid id,
        [FromBody] RejectCashPaymentRequest request,
        CancellationToken cancellationToken)
        => Ok(await paymentService.RejectCashPaymentAsync(id, request, CurrentUserId(), cancellationToken));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsCustomer() => User.IsInRole("COMPANY");
    private bool CanViewAll() => User.IsInRole("MANAGER") || User.IsInRole("STOREKEEPER") || User.IsInRole("LEAD_STOREKEEPER") || User.IsInRole("TECHNICIAN");
    private bool IsTechnicianOnly() => User.IsInRole("TECHNICIAN") && !User.IsInRole("MANAGER") && !User.IsInRole("SALES");
}
