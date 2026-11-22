using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
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
public sealed class RentalContractController(ContractService contractService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RentalContractResponse>>> GetContracts(
        [FromQuery] string? keyword,
        [FromQuery] string? status,
        [FromQuery] bool mine,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.GetContractsAsync(keyword, status, mine, CurrentUserId(), cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RentalContractDetailResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await contractService.GetDetailAsync(id, CurrentUserId(), cancellationToken));
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

    /// <summary>Cấp vé upload ảnh giấy tờ / hợp đồng đã ký lên Cloudinary (tối đa 6 ảnh, 5 MB/ảnh).</summary>
    [HttpPost("upload-ticket")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<FileUploadTicket>> CreateUploadTicket(
        [FromBody] FileUploadRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await contractService.CreateUploadTicketAsync(request, CurrentUserId(), cancellationToken));
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

    /// <summary>Sales tải bản hợp đồng đã ký (PENDING_SIGNATURE → SIGNED).</summary>
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
}
