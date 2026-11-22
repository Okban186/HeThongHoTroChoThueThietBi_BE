using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

public sealed class UpdateUserStatusRequest
{
    // ACTIVE, INACTIVE hoặc LOCKED.
    [Required(ErrorMessage = "Vui lòng chọn trạng thái.")]
    public string Status { get; init; } = string.Empty;
}
