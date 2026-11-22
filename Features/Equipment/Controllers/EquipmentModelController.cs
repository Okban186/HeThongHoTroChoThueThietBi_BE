using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Controllers;

[ApiController]
[Route("api/equipment-models")]
[Authorize(Roles = "ADMIN,MANAGER,SALES,STOREKEEPER,TECHNICIAN")]
public sealed class EquipmentModelController(EquipmentModelService equipmentModelService) : ControllerBase
{
    /// <summary>Danh sách model đang hoạt động, kèm số lượng khả dụng hôm nay.</summary>
    [HttpGet]
    public async Task<ActionResult<List<EquipmentModelResponse>>> GetModels(
        [FromQuery] string? keyword,
        CancellationToken cancellationToken)
    {
        return Ok(await equipmentModelService.GetActiveModelsAsync(keyword, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EquipmentModelResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        return Ok(await equipmentModelService.GetByIdAsync(id, cancellationToken));
    }

    /// <summary>
    /// Số lượng khả dụng của một model trong khoảng ngày.
    /// Ví dụ: GET /api/equipment-models/{id}/availability?from=2026-12-01&amp;to=2026-12-10
    /// </summary>
    [HttpGet("{id:guid}/availability")]
    public async Task<ActionResult<EquipmentAvailabilityResponse>> GetAvailability(
        Guid id,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? excludeReservationId,
        CancellationToken cancellationToken)
    {
        var (fromDate, toDate) = RequireDateRange(from, to);

        return Ok(await equipmentModelService.GetAvailabilityAsync(
            id,
            fromDate,
            toDate,
            excludeReservationId,
            cancellationToken));
    }

    /// <summary>Số lượng khả dụng của nhiều model trong cùng một khoảng ngày.</summary>
    [HttpPost("availability")]
    public async Task<ActionResult<List<EquipmentAvailabilityResponse>>> CheckAvailability(
        [FromBody] CheckEquipmentAvailabilityRequest request,
        CancellationToken cancellationToken)
    {
        var (fromDate, toDate) = RequireDateRange(request.From, request.To);

        return Ok(await equipmentModelService.CheckAvailabilityAsync(
            request.EquipmentModelIds,
            fromDate,
            toDate,
            request.ExcludeReservationId,
            cancellationToken));
    }

    private static (DateOnly From, DateOnly To) RequireDateRange(DateOnly? from, DateOnly? to)
    {
        if (from is null)
            throw new AppException("FROM_DATE_REQUIRED", "Vui lòng chọn ngày bắt đầu.", StatusCodes.Status400BadRequest, "from");

        if (to is null)
            throw new AppException("TO_DATE_REQUIRED", "Vui lòng chọn ngày kết thúc.", StatusCodes.Status400BadRequest, "to");

        return (from.Value, to.Value);
    }
}
