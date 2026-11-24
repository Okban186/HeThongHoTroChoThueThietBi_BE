using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Controllers;

/// <summary>
/// Phiếu nhận trả thiết bị: kỹ thuật viên thu hồi máy về kho, thủ kho kiểm từng serial
/// (đã nhận + tình trạng / thất lạc) và lập phiếu. Quản lý, kỹ thuật viên được xem.
/// </summary>
[ApiController]
[Route("api/equipment-returns")]
[Authorize(Roles = ReadRoles)]
public sealed class EquipmentReturnController(EquipmentReturnService returnService) : ControllerBase
{
    private const string ReadRoles = "LEAD_STOREKEEPER,STOREKEEPER,MANAGER,TECHNICIAN";
    private const string WriteRoles = "LEAD_STOREKEEPER,STOREKEEPER";

    /// <summary>Hợp đồng còn máy ở chỗ khách (đã giao, chưa nhận lại).</summary>
    [HttpGet("contracts")]
    public async Task<ActionResult<List<ReturnContractResponse>>> GetContracts(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        return Ok(await returnService.GetContractsAsync(keyword, cancellationToken));
    }

    [HttpGet("contracts/{contractId:guid}")]
    public async Task<ActionResult<ReturnContractDetailResponse>> GetContract(
        Guid contractId,
        CancellationToken cancellationToken)
    {
        return Ok(await returnService.GetContractDetailAsync(contractId, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<List<EquipmentReturnResponse>>> GetReturns(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        return Ok(await returnService.GetReturnsAsync(keyword, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EquipmentReturnDetailResponse>> GetReturn(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await returnService.GetReturnDetailAsync(id, cancellationToken));
    }

    [HttpPost]
    [Authorize(Roles = WriteRoles)]
    public async Task<ActionResult<EquipmentReturnDetailResponse>> Create(
        CreateEquipmentReturnRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await returnService.CreateAsync(request, CurrentUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetReturn), new { id = detail.Return.Id }, detail);
    }

    // Claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
