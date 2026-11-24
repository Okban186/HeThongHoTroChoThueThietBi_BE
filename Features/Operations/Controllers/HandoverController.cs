using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Controllers;

/// <summary>
/// Phiếu bàn giao thiết bị. Thủ kho (trưởng và nhân viên) soạn phiếu: chọn máy theo serial,
/// soạn xong thì chờ duyệt. Quản lý và kỹ thuật viên được xem (duyệt/giao do phần của họ).
/// </summary>
[ApiController]
[Route("api/handovers")]
[Authorize(Roles = ReadRoles)]
public sealed class HandoverController(HandoverService handoverService) : ControllerBase
{
    private const string ReadRoles = "LEAD_STOREKEEPER,STOREKEEPER,MANAGER,TECHNICIAN";
    private const string WriteRoles = "LEAD_STOREKEEPER,STOREKEEPER";

    /// <summary>Hợp đồng đã ký cần soạn bàn giao, kèm tiến độ xếp máy.</summary>
    [HttpGet("contracts")]
    public async Task<ActionResult<List<HandoverContractResponse>>> GetContracts(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        return Ok(await handoverService.GetContractsToPrepareAsync(keyword, cancellationToken));
    }

    [HttpGet("contracts/{contractId:guid}")]
    public async Task<ActionResult<HandoverContractDetailResponse>> GetContract(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        return Ok(await handoverService.GetContractDetailAsync(contractId, cancellationToken));
    }

    /// <summary>Máy sẵn sàng (AVAILABLE, không hỏng) của một model để chọn vào phiếu.</summary>
    [HttpGet("available-equipments")]
    public async Task<ActionResult<List<AvailableEquipmentResponse>>> GetAvailableEquipments(
        [FromQuery] Guid equipmentModelId,
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        return Ok(await handoverService.GetAvailableEquipmentsAsync(equipmentModelId, keyword, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<List<HandoverNoteResponse>>> GetNotes(
        [FromQuery] string? status,
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        return Ok(await handoverService.GetNotesAsync(status, keyword, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HandoverNoteDetailResponse>> GetNote(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await handoverService.GetNoteDetailAsync(id, cancellationToken));
    }

    [HttpPost]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Create(
        CreateHandoverRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await handoverService.CreateAsync(request, CurrentUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetNote), new { id = detail.Handover.Id }, detail);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Update(
        Guid id,
        SaveHandoverRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await handoverService.UpdateAsync(id, request, cancellationToken));
    }

    /// <summary>Soạn xong: PREPARING → PENDING_APPROVAL (chờ quản lý duyệt xuất kho).</summary>
    [HttpPost("{id:guid}/submit")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Submit(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await handoverService.SubmitAsync(id, cancellationToken));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Cancel(
        Guid id,
        CancelHandoverRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await handoverService.CancelAsync(id, request, CurrentUserId(), cancellationToken));
    }

    // Claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
