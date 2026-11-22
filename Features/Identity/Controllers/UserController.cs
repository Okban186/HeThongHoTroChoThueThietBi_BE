using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Identity.DTOs;
using HeThongHoTroChoThueThietBi.Features.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Controllers;

/// <summary>Quản lý tài khoản (chỉ Admin).</summary>
[ApiController]
[Route("api/users")]
[Authorize(Roles = UserManagementService.AdminRoleCode)]
public sealed class UserController(UserManagementService userManagementService) : ControllerBase
{
    /// <summary>Danh sách tài khoản. Lọc theo từ khóa, role (ví dụ SALES), trạng thái (ACTIVE/INACTIVE/LOCKED).</summary>
    [HttpGet]
    public async Task<ActionResult<List<UserAccountResponse>>> GetUsers(
        [FromQuery] string? keyword,
        [FromQuery] string? role,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        return Ok(await userManagementService.GetUsersAsync(keyword, role, status, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserAccountResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await userManagementService.GetByIdAsync(id, cancellationToken));
    }

    /// <summary>Tạo tài khoản nhân viên. Mã nhân viên tự sinh theo role.</summary>
    [HttpPost]
    public async Task<ActionResult<UserAccountResponse>> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.CreateAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = user.Id }, user);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserAccountResponse>> Update(
        Guid id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await userManagementService.UpdateAsync(id, request, cancellationToken));
    }

    [HttpPut("{id:guid}/role")]
    public async Task<ActionResult<UserAccountResponse>> UpdateRole(
        Guid id,
        [FromBody] UpdateUserRoleRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await userManagementService.UpdateRoleAsync(id, request, CurrentUserId(), cancellationToken));
    }

    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<UserAccountResponse>> UpdateStatus(
        Guid id,
        [FromBody] UpdateUserStatusRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await userManagementService.UpdateStatusAsync(id, request, CurrentUserId(), cancellationToken));
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(
        Guid id,
        [FromBody] ResetUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await userManagementService.ResetPasswordAsync(id, request, CurrentUserId(), cancellationToken);

        return NoContent();
    }

    // [Authorize] đảm bảo đã xác thực; claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
