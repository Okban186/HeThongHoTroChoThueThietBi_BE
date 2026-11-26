using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Features.Finance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Finance.Services;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Controllers;

/// <summary>
/// Hợp đồng thuê. Sales tạo/sửa/gửi duyệt/tải bản ký/hủy; Manager duyệt/từ chối.
/// </summary>
[ApiController]
[Route("api/rental-contracts")]
[Authorize(Roles = "SALES,MANAGER")]
public sealed class RentalContractController(
    ContractService contractService,
    PaymentService paymentService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RentalContractResponse>>> GetContracts(
        [FromQuery] string? keyword,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.GetContractsAsync(keyword, status, CurrentUserId(), CanViewAll(), cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RentalContractDetailResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await contractService.GetDetailForViewerAsync(id, CurrentUserId(), CanViewAll(), cancellationToken));
    }

    /// <summary>Tổng quan chi phí, các đợt tiền thuê, khoản phạt và giao dịch thanh toán của hợp đồng.</summary>
    [HttpGet("{id:guid}/billing")]
    public async Task<ActionResult<ContractBillingSummaryResponse>> GetBilling(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await paymentService.GetContractBillingAsync(id, CurrentUserId(), isCustomer: false, CanViewAll(), cancellationToken));
    }

    /// <summary>
    /// Xuất hợp đồng PDF điền sẵn thông tin hai bên, thiết bị, dịch vụ, tiền để in và ký.
    /// Hợp đồng chưa được duyệt có dòng "BẢN NHÁP".
    /// </summary>
    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> ExportPdf(Guid id, CancellationToken cancellationToken)
    {
        var (fileName, content) = await contractService.ExportPdfAsync(id, CurrentUserId(), CanViewAll(), cancellationToken);

        return File(content, "application/pdf", fileName);
    }

    /// <summary>Giá trị đề xuất (loại thuê, đơn vị tính, đơn giá, cọc) để điền sẵn form tạo hợp đồng.</summary>
    [HttpGet("suggestion")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<RentalContractSuggestionResponse>> GetSuggestion(
        [FromQuery] Guid reservationId,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.GetSuggestionAsync(reservationId, CurrentUserId(), cancellationToken));
    }

    /// <summary>Báo giá phí vận chuyển tự động theo khoảng cách và ưu đãi thuê nhiều (Sales chỉ xem).</summary>
    [HttpPost("shipping-quote")]
    [Authorize(Roles = "SALES,MANAGER")]
    public async Task<ActionResult<ShippingQuoteResponse>> GetShippingQuote(
        [FromBody] ShippingQuoteRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.GetShippingQuoteAsync(request, CurrentUserId(), CanViewAll(), cancellationToken));
    }

    /// <summary>Cấp vé upload ảnh giấy tờ lên Cloudinary (tối đa 6 ảnh jpg/png/webp, 5 MB/ảnh).</summary>
    [HttpPost("upload-ticket")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<FileUploadTicket>> CreateUploadTicket(
        [FromBody] FileUploadRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.CreateUploadTicketAsync(request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Cấp vé upload bản hợp đồng đã ký (1 file PDF, tối đa 10 MB). Xác nhận qua confirm-upload.</summary>
    [HttpPost("signed-upload-ticket")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<FileUploadTicket>> CreateSignedUploadTicket(CancellationToken cancellationToken)
    {
        return Ok(await contractService.CreateSignedUploadTicketAsync(CurrentUserId(), cancellationToken));
    }

    /// <summary>Xác nhận ảnh đã upload; trả storageFileId của từng ảnh để gắn vào hợp đồng.</summary>
    [HttpPost("confirm-upload")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ConfirmFileUploadResponse>> ConfirmUpload(
        [FromBody] ConfirmFileUploadRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.ConfirmUploadAsync(request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Tạo hợp đồng (DRAFT) từ phiếu giữ chỗ đã xác nhận. Phiếu chuyển CONVERTED.</summary>
    [HttpPost]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<RentalContractDetailResponse>> Create(
        [FromBody] CreateRentalContractRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await contractService.CreateAsync(request, CurrentUserId(), cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = detail.Contract.Id }, detail);
    }

    /// <summary>Sửa hợp đồng khi Bản nháp hoặc Bị từ chối.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<RentalContractDetailResponse>> Update(
        Guid id,
        [FromBody] UpdateRentalContractRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.UpdateAsync(id, request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Gửi duyệt (DRAFT/REJECTED → PENDING_APPROVAL).</summary>
    [HttpPost("{id:guid}/submit")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<RentalContractDetailResponse>> Submit(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await contractService.SubmitAsync(id, CurrentUserId(), cancellationToken));
    }

    /// <summary>Manager duyệt (PENDING_APPROVAL → PENDING_SIGNATURE).</summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<RentalContractDetailResponse>> Approve(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await contractService.ApproveAsync(id, CurrentUserId(), cancellationToken));
    }

    /// <summary>Manager từ chối kèm lý do (PENDING_APPROVAL → REJECTED).</summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<RentalContractDetailResponse>> Reject(
        Guid id,
        [FromBody] RejectRentalContractRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.RejectAsync(id, request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Sales tải bản hợp đồng đã ký dạng PDF (PENDING_SIGNATURE → SIGNED).</summary>
    [HttpPost("{id:guid}/signed")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<RentalContractDetailResponse>> MarkSigned(
        Guid id,
        [FromBody] MarkRentalContractSignedRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.MarkSignedAsync(id, request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Hủy hợp đồng trước khi ký.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<RentalContractDetailResponse>> Cancel(
        Guid id,
        [FromBody] CancelRentalContractRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.CancelAsync(id, request, CurrentUserId(), cancellationToken));
    }

    // Claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Manager xem mọi hợp đồng; Sales chỉ xem hợp đồng do mình tạo.
    private bool CanViewAll() => User.IsInRole("MANAGER");
}
