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
}
