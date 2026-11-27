using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Maintenance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Controllers;

/// <summary>
/// Quản lý & thực hiện phiếu kiểm tra / bảo trì thiết bị tại kho:
/// - KTV thấy phiếu chưa có người nhận (AssignedTechnicianStaffId == null) hoặc phiếu do chính mình tiếp nhận.
/// - KTV tự bấm Tiếp nhận (claim) để khóa quyền xử lý độc quyền, không bị chồng chéo giữa các KTV.
/// - Manager có thể xem toàn bộ và chỉ định đích danh KTV khi cần (assign).
/// </summary>
[ApiController]
[Route("api/technician/maintenance-tasks")]
[Authorize(Roles = "TECHNICIAN,MANAGER,LEAD_STOREKEEPER,STOREKEEPER")]
public sealed class TechnicianMaintenanceController(TechnicianMaintenanceService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<MaintenanceTaskSummaryResponse>>> GetTasks(
        [FromQuery] string? status,
        [FromQuery] string? type,
        [FromQuery] string? keyword,
        CancellationToken ct)
        => Ok(await service.GetTasksAsync(
            status, type, keyword, CurrentUserId(), IsTechnicianOnly(), ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenanceTaskDetailResponse>> GetTaskDetail(Guid id, CancellationToken ct)
        => Ok(await service.GetTaskDetailAsync(id, CurrentUserId(), IsTechnicianOnly(), ct));

    [HttpPost]
    public async Task<ActionResult<MaintenanceTaskDetailResponse>> CreateTask(
        [FromBody] CreateMaintenanceTaskRequest request,
        CancellationToken ct)
    {
        var detail = await service.CreateTaskAsync(request, ct);
        return CreatedAtAction(nameof(GetTaskDetail), new { id = detail.Task.Id }, detail);
    }

    /// <summary>KTV tự tiếp nhận phiếu bảo trì chưa có người nhận (khóa độc quyền, KTV khác không thấy nữa).</summary>
    [HttpPost("{id:guid}/claim")]
    [Authorize(Roles = "TECHNICIAN")]
    public async Task<ActionResult<MaintenanceTaskDetailResponse>> ClaimTask(Guid id, CancellationToken ct)
        => Ok(await service.ClaimTaskAsync(id, CurrentUserId(), ct));

    /// <summary>Manager chỉ định đích danh KTV phụ trách phiếu bảo trì gấp/khó.</summary>
    [HttpPost("{id:guid}/assign")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<MaintenanceTaskDetailResponse>> AssignTask(
        Guid id,
        [FromBody] AssignMaintenanceTaskRequest request,
        CancellationToken ct)
        => Ok(await service.AssignTaskByManagerAsync(id, request.TechnicianStaffId!.Value, ct));

    [HttpPost("{id:guid}/start")]
    [Authorize(Roles = "TECHNICIAN")]
    public async Task<ActionResult<MaintenanceTaskDetailResponse>> StartTask(Guid id, CancellationToken ct)
        => Ok(await service.StartTaskAsync(id, CurrentUserId(), ct));

    [HttpPost("{id:guid}/complete")]
    [Authorize(Roles = "TECHNICIAN")]
    public async Task<ActionResult<CompleteMaintenanceTaskResponse>> CompleteTask(
        Guid id,
        [FromBody] CompleteMaintenanceTaskRequest request,
        CancellationToken ct)
        => Ok(await service.CompleteTaskAsync(id, request, CurrentUserId(), ct));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsTechnicianOnly() =>
        User.IsInRole("TECHNICIAN") &&
        !User.IsInRole("MANAGER") &&
        !User.IsInRole("LEAD_STOREKEEPER") &&
        !User.IsInRole("STOREKEEPER");
}
