using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

/// <summary>
/// Cập nhật thông tin cá nhân. Position/HireDate chỉ áp dụng cho tài khoản nhân viên.
/// </summary>
public sealed class UpdateUserRequest
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên.")]
    [MaxLength(150)]
    public string FullName { get; init; } = string.Empty;

    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    [MaxLength(255)]
    public string? Email { get; init; }

    [RegularExpression("^[0-9+ ]{8,20}$", ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string? Phone { get; init; }

    [MaxLength(100)]
    public string? Position { get; init; }

    public DateOnly? HireDate { get; init; }
}
