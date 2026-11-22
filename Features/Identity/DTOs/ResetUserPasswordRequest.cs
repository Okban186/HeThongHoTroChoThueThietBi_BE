using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

public sealed class ResetUserPasswordRequest
{
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới.")]
    [MinLength(6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự.")]
    [MaxLength(100)]
    public string NewPassword { get; init; } = string.Empty;
}
