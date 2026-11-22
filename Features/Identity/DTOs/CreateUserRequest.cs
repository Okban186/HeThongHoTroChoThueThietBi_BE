using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

/// <summary>
/// Admin tạo tài khoản nhân viên. Mã nhân viên được hệ thống tự sinh theo role.
/// </summary>
public sealed class CreateUserRequest
{
    [Required(ErrorMessage = "Vui lòng nhập tên đăng nhập.")]
    [RegularExpression(
        "^[a-zA-Z0-9._-]{3,50}$",
        ErrorMessage = "Tên đăng nhập gồm 3–50 ký tự: chữ không dấu, số, dấu chấm, gạch dưới hoặc gạch ngang.")]
    public string Username { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mật khẩu.")]
    [MinLength(6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự.")]
    [MaxLength(100)]
    public string Password { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập họ tên.")]
    [MaxLength(150)]
    public string FullName { get; init; } = string.Empty;

    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    [MaxLength(255)]
    public string? Email { get; init; }

    [RegularExpression("^[0-9+ ]{8,20}$", ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string? Phone { get; init; }

    // Một trong: ADMIN, MANAGER, SALES, STOREKEEPER, TECHNICIAN.
    [Required(ErrorMessage = "Vui lòng chọn vai trò.")]
    public string RoleCode { get; init; } = string.Empty;

    [MaxLength(100)]
    public string? Position { get; init; }

    public DateOnly? HireDate { get; init; }
}
