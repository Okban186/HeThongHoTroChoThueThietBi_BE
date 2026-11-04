using HeThongHoTroChoThueThietBi.Features.Identity.Entities;

namespace HeThongHoTroChoThueThietBi.Features.Notifications;

public class UserDevice
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string FcmToken { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public string Platform { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}