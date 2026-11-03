using System.ComponentModel.DataAnnotations;
namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

public sealed class RefreshRequest
{
    [Required] public string RefreshToken { get; init; } = string.Empty;
}
