using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Controllers;

[ApiController]
[Route("api/manager")]
[Authorize(Roles = "MANAGER")]
public sealed class ManagerOperationsController(ManagerOperationsService service) : ControllerBase
{
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("technicians/availability")]
    public async Task<ActionResult<List<TechnicianAvailabilityResponse>>> GetTechnicians(
        [FromQuery] DateTimeOffset? startAt,
        [FromQuery] int? durationMinutes,
        [FromQuery] Guid? excludeAssignmentId,
        CancellationToken ct)
        => Ok(await service.GetTechniciansAvailabilityAsync(startAt, durationMinutes, excludeAssignmentId, ct));

    /// <summary>Danh sách hợp đồng PREPARING hoặc PARTIALLY_DELIVERED (đã thu hồi xong máy lỗi) chờ Manager lập/ghép chuyến giao.</summary>
    [HttpGet("delivery-trips/unscheduled-contracts")]
    public async Task<ActionResult<List<UnscheduledDeliveryContractResponse>>> GetUnscheduledContracts(CancellationToken ct)
        => Ok(await service.GetUnscheduledDeliveryContractsAsync(CurrentUserId(), ct));

    /// <summary>Tổng quan các đơn giao thiếu (chờ thu hồi máy lỗi vs sẵn sàng tạo lịch giao bù) cho Manager.</summary>
    [HttpGet("delivery-trips/supplementary-overview")]
    public async Task<ActionResult<List<SupplementaryDeliveryOverviewResponse>>> GetSupplementaryOverview(CancellationToken ct)
        => Ok(await service.GetSupplementaryDeliveryOverviewAsync(CurrentUserId(), ct));

    [HttpGet("delivery-trips/mergeable")]
    public async Task<ActionResult<List<MergeableTripResponse>>> GetMergeableTrips(
        [FromQuery] DateOnly? date, CancellationToken ct)
        => Ok(await service.GetMergeableTripsAsync(
            date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)), CurrentUserId(), ct));

    [HttpPost("delivery-trips/preview")]
    public async Task<ActionResult<OptimizedTripResponse>> PreviewTrip(
        [FromBody] PreviewDeliveryTripRequest req, CancellationToken ct)
        => Ok(await service.PreviewDeliveryTripAsync(req, CurrentUserId(), ct));

    [HttpPost("delivery-trips")]
    public async Task<ActionResult<DailyScheduleItemResponse>> CreateTrip(
        [FromBody] CreateDeliveryTripRequest req, CancellationToken ct)
        => Ok(await service.CreateDeliveryTripAsync(req, CurrentUserId(), ct));

    [HttpPost("delivery-trips/{assignmentId:guid}/merge")]
    public async Task<ActionResult<DailyScheduleItemResponse>> MergeIntoTrip(
        Guid assignmentId, [FromBody] MergeIntoDeliveryTripRequest req, CancellationToken ct)
        => Ok(await service.MergeIntoDeliveryTripAsync(assignmentId, req, CurrentUserId(), ct));

    [HttpGet("schedule")]
    public async Task<ActionResult<List<DailyScheduleItemResponse>>> GetSchedule(
        [FromQuery] DateOnly? date, [FromQuery] Guid? staffId, CancellationToken ct)
        => Ok(await service.GetDailyScheduleAsync(
            date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)), staffId, ct));

    [HttpGet("incidents")]
    public async Task<ActionResult<List<ManagerIncidentResponse>>> GetIncidents(
        [FromQuery] string? status, CancellationToken ct)
        => Ok(await service.GetIncidentsAsync(status, ct));

    [HttpPost("incidents/{id:guid}/assign-remote")]
    public async Task<IActionResult> AssignRemote(
        Guid id, [FromBody] AssignRemoteIncidentRequest req, CancellationToken ct)
    {
        await service.AssignOrReassignRemoteIncidentAsync(id, req, CurrentUserId(), ct);
        return NoContent();
    }

    [HttpPost("incidents/{id:guid}/dispatch")]
    public async Task<IActionResult> DispatchIncident(
        Guid id, [FromBody] DispatchIncidentTeamRequest req, CancellationToken ct)
    {
        await service.DispatchIncidentTeamAsync(id, req, CurrentUserId(), ct);
        return NoContent();
    }

    [HttpGet("approvals/replacements")]
    public async Task<ActionResult<List<PendingReplacementResponse>>> GetPendingReplacements(CancellationToken ct)
        => Ok(await service.GetPendingReplacementsAsync(CurrentUserId(), ct));

    [HttpPost("approvals/replacements/{id:guid}/decide")]
    public async Task<IActionResult> DecideReplacement(
        Guid id, [FromBody] ApprovalDecisionRequest req, CancellationToken ct)
    {
        await service.DecideReplacementAsync(id, req, CurrentUserId(), ct);
        return NoContent();
    }

    [HttpGet("approvals/handovers")]
    public async Task<ActionResult<List<PendingHandoverResponse>>> GetPendingHandovers(CancellationToken ct)
        => Ok(await service.GetPendingHandoversAsync(CurrentUserId(), ct));

    [HttpPost("approvals/handovers/{id:guid}/decide")]
    public async Task<IActionResult> DecideHandover(
        Guid id, [FromBody] ApprovalDecisionRequest req, CancellationToken ct)
    {
        await service.DecideHandoverAsync(id, req, CurrentUserId(), ct);
        return NoContent();
    }

    [HttpPost("handovers/{id:guid}/record-failure")]
    public async Task<IActionResult> RecordHandoverFailure(
        Guid id, [FromBody] RecordDeliveryFailureRequest req, CancellationToken ct)
    {
        await service.RecordDeliveryFailureAsync(id, req, CurrentUserId(), ct);
        return NoContent();
    }

    [HttpGet("approvals/disposals")]
    public async Task<ActionResult<List<PendingDisposalResponse>>> GetPendingDisposals(CancellationToken ct)
        => Ok(await service.GetPendingDisposalsAsync(ct));

    [HttpPost("approvals/disposals/{id:guid}/decide")]
    public async Task<IActionResult> DecideDisposal(
        Guid id, [FromBody] ApprovalDecisionRequest req, CancellationToken ct)
    {
        await service.DecideDisposalAsync(id, req, CurrentUserId(), ct);
        return NoContent();
    }

    /// <summary>Danh sách đề xuất phạt / bồi thường sự cố do KTV gửi lên chờ Manager duyệt.</summary>
    [HttpGet("approvals/incident-charges")]
    public async Task<ActionResult<List<PendingIncidentChargeResponse>>> GetPendingIncidentCharges(CancellationToken ct)
        => Ok(await service.GetPendingIncidentChargesAsync(CurrentUserId(), ct));

    /// <summary>Manager xét duyệt khoản phạt sự cố: cấn trừ tiền cọc, thu riêng, hoặc miễn phạt.</summary>
    [HttpPost("approvals/incident-charges/{id:guid}/decide")]
    public async Task<ActionResult<DecideIncidentChargeResponse>> DecideIncidentCharge(
        Guid id, [FromBody] DecideIncidentChargeRequest req, CancellationToken ct)
        => Ok(await service.DecideIncidentChargeAsync(id, req, CurrentUserId(), ct));

    // --- CHUYẾN THU HỒI CUỐI KỲ / HẾT HẠN / HỦY HĐ (RETRIEVAL) ---

    /// <summary>Danh sách hợp đồng còn thiết bị đang cho thuê ngoài hiện trường cần lập chuyến thu hồi.</summary>
    [HttpGet("retrieval-trips/pending-contracts")]
    public async Task<ActionResult<List<PendingRetrievalContractResponse>>> GetPendingRetrievalContracts(CancellationToken ct)
        => Ok(await service.GetPendingRetrievalContractsAsync(CurrentUserId(), ct));

    /// <summary>Xem trước lộ trình tối ưu chuyến thu hồi thiết bị.</summary>
    [HttpPost("retrieval-trips/preview")]
    public async Task<ActionResult<OptimizedTripResponse>> PreviewRetrievalTrip(
        [FromBody] PreviewRetrievalTripRequest req, CancellationToken ct)
        => Ok(await service.PreviewRetrievalTripAsync(req, CurrentUserId(), ct));

    /// <summary>Tạo chuyến thu hồi thiết bị (RETRIEVAL) cử đội KTV đi thu hồi về kho.</summary>
    [HttpPost("retrieval-trips")]
    public async Task<ActionResult<DailyScheduleItemResponse>> CreateRetrievalTrip(
        [FromBody] CreateRetrievalTripRequest req, CancellationToken ct)
        => Ok(await service.CreateRetrievalTripAsync(req, CurrentUserId(), ct));
}
