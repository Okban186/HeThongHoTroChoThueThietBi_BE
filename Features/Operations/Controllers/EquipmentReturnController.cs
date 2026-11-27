using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Controllers;

[ApiController]
[Route("api/equipment-returns")]
[Authorize(Roles = ReadRoles)]
public sealed class EquipmentReturnController(EquipmentReturnService returnService) : ControllerBase
{
    private const string ReadRoles = "LEAD_STOREKEEPER,STOREKEEPER,MANAGER,TECHNICIAN";
    private const string WriteRoles = "LEAD_STOREKEEPER,STOREKEEPER";

    [HttpGet("contracts")]
    public async Task<ActionResult<List<ReturnContractResponse>>> GetContracts(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
        => Ok(await returnService.GetContractsAsync(keyword, cancellationToken));

    [HttpGet("contracts/{contractId:guid}")]
    public async Task<ActionResult<ReturnContractDetailResponse>> GetContract(
        Guid contractId,
        CancellationToken cancellationToken)
        => Ok(await returnService.GetContractDetailAsync(
            contractId, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    [HttpGet]
    public async Task<ActionResult<List<EquipmentReturnResponse>>> GetReturns(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
        => Ok(await returnService.GetReturnsAsync(
            keyword, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EquipmentReturnDetailResponse>> GetReturn(Guid id, CancellationToken cancellationToken)
        => Ok(await returnService.GetReturnDetailAsync(
            id, CurrentUserId(), IsStorekeeperOnly(), cancellationToken));

    /// <summary>Thủ kho tiếp nhận phụ trách xử lý phiếu thu hồi chưa có người nhận.</summary>
    [HttpPost("{id:guid}/claim")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<EquipmentReturnDetailResponse>> Claim(Guid id, CancellationToken cancellationToken)
        => Ok(await returnService.ClaimAsync(id, CurrentUserId(), cancellationToken));

    [HttpPost]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<EquipmentReturnDetailResponse>> Create(
        CreateEquipmentReturnRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await returnService.CreateAsync(request, CurrentUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetReturn), new { id = detail.Return.Id }, detail);
    }

    [HttpPost("{id:guid}/confirm-processing")]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<EquipmentReturnDetailResponse>> ConfirmProcessing(
        Guid id,
        ConfirmProcessingReturnRequest request,
        CancellationToken cancellationToken)
        => Ok(await returnService.ConfirmProcessingAsync(id, request, CurrentUserId(), cancellationToken));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsStorekeeperOnly() =>
        !User.IsInRole("MANAGER") && !User.IsInRole("TECHNICIAN") &&
        (User.IsInRole("STOREKEEPER") || User.IsInRole("LEAD_STOREKEEPER"));
}
