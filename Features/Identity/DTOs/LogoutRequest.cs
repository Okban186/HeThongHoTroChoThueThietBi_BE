using System.ComponentModel.DataAnnotations;
namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

public sealed class LogoutRequest
{
    [Required] public string RefreshToken { get; init; } = string.Empty;
}
