using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

/// <summary>
/// Đổi role của tài khoản nhân viên (mỗi tài khoản đúng 1 role).
/// </summary>
public sealed class UpdateUserRoleRequest
{
    // Một trong: ADMIN, MANAGER, SALES, STOREKEEPER, TECHNICIAN.
    [Required(ErrorMessage = "Vui lòng chọn vai trò.")]
    public string RoleCode { get; init; } = string.Empty;
}
