using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Maintenance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Controllers;

/// <summary>
/// Cổng xử lý sự cố thiết bị cho Kỹ thuật viên (Hỗ trợ Online & Hiện trường).
/// Ảnh minh chứng upload qua POST /api/technician/upload-ticket?folder=incident + confirm-upload.
/// </summary>
[ApiController]
[Route("api/technician/incidents")]
[Authorize(Roles = "TECHNICIAN")]
public sealed class TechnicianIncidentController(TechnicianIncidentService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<TechnicianIncidentSummaryResponse>>> GetMyIncidents(
        [FromQuery] string? status,
        CancellationToken ct)
        => Ok(await service.GetMyIncidentsAsync(CurrentUserId(), status, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TechnicianIncidentDetailResponse>> GetIncidentDetail(Guid id, CancellationToken ct)
        => Ok(await service.GetIncidentDetailAsync(id, CurrentUserId(), ct));

    /// <summary>Chỉ KTV trưởng: xác nhận đã tới hiện trường xử lý sự cố (DISPATCHING → ON_SITE).</summary>
    [HttpPost("{id:guid}/arrive")]
    public async Task<ActionResult<TechnicianIncidentDetailResponse>> StartOnSite(Guid id, CancellationToken ct)
        => Ok(await service.StartOnSiteAsync(id, CurrentUserId(), ct));

    /// <summary>Chốt kết quả xử lý sự cố (RESOLVED hoặc RETURN_REQUIRED tự sinh phiếu thu hồi PROCESSING).</summary>
    [HttpPost("{id:guid}/resolve")]
    public async Task<ActionResult<TechnicianResolveIncidentResponse>> ResolveIncident(
        Guid id,
        [FromBody] TechnicianResolveIncidentRequest request,
        CancellationToken ct)
        => Ok(await service.ResolveIncidentAsync(id, request, CurrentUserId(), ct));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
