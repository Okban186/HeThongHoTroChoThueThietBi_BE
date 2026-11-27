using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Controllers;

[ApiController]
[Route("api/handovers")]
[Authorize(Roles = ReadRoles)]
public sealed class HandoverController(HandoverService handoverService) : ControllerBase
{
    private const string ReadRoles = "LEAD_STOREKEEPER,STOREKEEPER,MANAGER,TECHNICIAN";
    private const string WriteRoles = "LEAD_STOREKEEPER,STOREKEEPER";

    [HttpGet("contracts")]
    public async Task<ActionResult<List<HandoverContractResponse>>> GetContracts(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
        => Ok(await handoverService.GetContractsToPrepareAsync(
            keyword, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    [HttpGet("contracts/{contractId:guid}")]
    public async Task<ActionResult<HandoverContractDetailResponse>> GetContract(
        Guid contractId,
        CancellationToken cancellationToken)
        => Ok(await handoverService.GetContractDetailAsync(
            contractId, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    [HttpGet("available-equipments")]
    public async Task<ActionResult<List<AvailableEquipmentResponse>>> GetAvailableEquipments(
        [FromQuery] Guid equipmentModelId,
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
        => Ok(await handoverService.GetAvailableEquipmentsAsync(equipmentModelId, keyword, cancellationToken));

    [HttpGet("lookup-serial")]
    public async Task<ActionResult<LookupSerialResponse>> LookupSerial(
        [FromQuery] string serial,
        [FromQuery] Guid contractId,
        CancellationToken cancellationToken)
        => Ok(await handoverService.LookupSerialAsync(serial, contractId, cancellationToken));

    [HttpGet]
    public async Task<ActionResult<List<HandoverNoteResponse>>> GetNotes(
        [FromQuery] string? status,
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
        => Ok(await handoverService.GetNotesAsync(
            status, keyword, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<HandoverNoteDetailResponse>> GetNote(Guid id, CancellationToken cancellationToken)
        => Ok(await handoverService.GetNoteDetailAsync(
            id, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    /// <summary>Thủ kho tiếp nhận phụ trách soạn phiếu bàn giao chưa có người nhận.</summary>
    [HttpPost("{id:guid}/claim")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Claim(Guid id, CancellationToken cancellationToken)
        => Ok(await handoverService.ClaimAsync(id, CurrentUserId(), cancellationToken));

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
        => Ok(await handoverService.UpdateAsync(id, request, CurrentUserId(), cancellationToken));

    [HttpPost("{id:guid}/submit")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Submit(Guid id, CancellationToken cancellationToken)
        => Ok(await handoverService.SubmitAsync(id, CurrentUserId(), cancellationToken));

    [HttpPost("{id:guid}/complete-delivery")]
    [Authorize(Roles = "TECHNICIAN,MANAGER")]
    public async Task<ActionResult<HandoverNoteDetailResponse>> CompleteDelivery(
        Guid id,
        CompleteHandoverDeliveryRequest request,
        CancellationToken cancellationToken)
        => Ok(await handoverService.CompleteDeliveryAsync(id, request, CurrentUserId(), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<HandoverNoteDetailResponse>> Cancel(
        Guid id,
        CancelHandoverRequest request,
        CancellationToken cancellationToken)
        => Ok(await handoverService.CancelAsync(id, request, CurrentUserId(), cancellationToken));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // Manager & Technician xem theo chuyến/hợp đồng; Thủ kho (STOREKEEPER & LEAD_STOREKEEPER) chỉ thấy phiếu chưa ai nhận hoặc do chính mình nhận.
    private bool IsStorekeeperOnly() =>
        !User.IsInRole("MANAGER") && !User.IsInRole("TECHNICIAN") &&
        (User.IsInRole("STOREKEEPER") || User.IsInRole("LEAD_STOREKEEPER"));
}
