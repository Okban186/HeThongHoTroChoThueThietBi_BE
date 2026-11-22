using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Organization.DTOs;

/// <summary>Sales tạo tài khoản đăng nhập (role COMPANY) cho người của công ty khách hàng.</summary>
public sealed class CreateCompanyAccountRequest
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
}
