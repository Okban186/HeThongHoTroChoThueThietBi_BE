using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Organization.DTOs;

public sealed class UpdateCompanyStatusRequest
{
    // ACTIVE hoặc INACTIVE.
    [Required(ErrorMessage = "Vui lòng chọn trạng thái.")]
    public string Status { get; init; } = string.Empty;
}
