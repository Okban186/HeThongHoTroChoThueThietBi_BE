using System.ComponentModel.DataAnnotations;
namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

public sealed class LoginRequest
{
    [Required, MaxLength(100)] public string Username { get; init; } = string.Empty;
    [Required] public string Password { get; init; } = string.Empty;
}
