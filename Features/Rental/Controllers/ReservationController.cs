using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Controllers;

/// <summary>
/// Phiếu giữ chỗ (đặt thuê). Sales và Manager được xem; chỉ Sales (người lập phiếu) thao tác.
/// </summary>
[ApiController]
[Route("api/reservations")]
[Authorize(Roles = "SALES,MANAGER")]
public sealed class ReservationController(ReservationService reservationService) : ControllerBase
{
    /// <summary>Danh sách phiếu. status: PENDING, APPROVED, CANCELLED, EXPIRED, CONVERTED; mine=true: phiếu của mình.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ReservationResponse>>> GetReservations(
        [FromQuery] string? keyword,
        [FromQuery] string? status,
        [FromQuery] bool mine,
        CancellationToken cancellationToken)
    {
        return Ok(await reservationService.GetReservationsAsync(keyword, status, mine, CurrentUserId(), cancellationToken));
    }

    /// <summary>Chi tiết phiếu kèm thiết bị và số lượng khả dụng của từng model.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ReservationDetailResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await reservationService.GetDetailAsync(id, CurrentUserId(), cancellationToken));
    }

    /// <summary>Lập phiếu giữ chỗ (PENDING). Mã phiếu PH-YYYY-NNNN và hạn giữ chỗ do hệ thống tạo.</summary>
    [HttpPost]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ReservationDetailResponse>> Create(
        [FromBody] CreateReservationRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await reservationService.CreateAsync(request, CurrentUserId(), cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = detail.Reservation.Id }, detail);
    }

    /// <summary>Sửa công ty, ngày thuê, người liên hệ, ghi chú (chỉ khi PENDING).</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ReservationDetailResponse>> Update(
        Guid id,
        [FromBody] UpdateReservationRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await reservationService.UpdateAsync(id, request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Thay toàn bộ danh sách thiết bị (chỉ khi PENDING), có kiểm tra số lượng khả dụng.</summary>
    [HttpPut("{id:guid}/items")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ReservationDetailResponse>> UpdateItems(
        Guid id,
        [FromBody] UpdateReservationItemsRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await reservationService.UpdateItemsAsync(id, request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Sales xác nhận phiếu (PENDING → APPROVED).</summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ReservationDetailResponse>> Approve(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await reservationService.ApproveAsync(id, CurrentUserId(), cancellationToken));
    }

    /// <summary>Mở lại phiếu đã xác nhận để chỉnh sửa (APPROVED → PENDING).</summary>
    [HttpPost("{id:guid}/reopen")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ReservationDetailResponse>> Reopen(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await reservationService.ReopenAsync(id, CurrentUserId(), cancellationToken));
    }

    /// <summary>Hủy phiếu (PENDING/APPROVED → CANCELLED), lý do được ghi vào ghi chú.</summary>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<ReservationDetailResponse>> Cancel(
        Guid id,
        [FromBody] CancelReservationRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await reservationService.CancelAsync(id, request, CurrentUserId(), cancellationToken));
    }

    // Claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
