using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Dashboard.DTOs;
using HeThongHoTroChoThueThietBi.Features.Dashboard.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Dashboard.Controllers;

/// <summary>Số liệu trang Tổng quan theo từng actor.</summary>
[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(DashboardService dashboardService) : ControllerBase
{
    /// <summary>Tổng quan của nhân viên kinh doanh đang đăng nhập.</summary>
    [HttpGet("sales")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<SalesDashboardResponse>> GetSalesDashboard(CancellationToken cancellationToken)
    {
        var salesId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return Ok(await dashboardService.GetSalesDashboardAsync(salesId, cancellationToken));
    }

    [HttpGet("storekeeper")]
    [Authorize(Roles = "LEAD_STOREKEEPER,STOREKEEPER")]
    public async Task<ActionResult<StorekeeperDashboardResponse>> GetStorekeeperDashboard(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return Ok(await dashboardService.GetStorekeeperDashboardAsync(userId, ct));
    }

    [HttpGet("manager")]
    [Authorize(Roles = "MANAGER")]
    public async Task<ActionResult<ManagerDashboardResponse>> GetManagerDashboard(CancellationToken ct)
    {
        var managerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return Ok(await dashboardService.GetManagerDashboardAsync(managerId, ct));
    }
}
