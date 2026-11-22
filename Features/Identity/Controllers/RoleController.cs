using HeThongHoTroChoThueThietBi.Features.Identity.DTOs;
using HeThongHoTroChoThueThietBi.Features.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Controllers;

/// <summary>Danh sách 6 role cố định của hệ thống.</summary>
[ApiController]
[Route("api/roles")]
[Authorize(Roles = UserManagementService.AdminRoleCode)]
public sealed class RoleController(UserManagementService userManagementService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<RoleResponse>>> GetRoles(CancellationToken cancellationToken)
    {
        return Ok(await userManagementService.GetRolesAsync(cancellationToken));
    }
}
