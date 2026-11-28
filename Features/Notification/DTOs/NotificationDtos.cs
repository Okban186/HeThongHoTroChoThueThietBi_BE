using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Notification.DTOs;

public sealed record NotificationResponse(
    Guid Id,
    Guid UserId,
    string Title,
    string Content,
    string? Type,
    bool IsRead,
    DateTimeOffset? ReadAt,
    DateTimeOffset CreatedAt);

public sealed record NotificationListResponse(
    int UnreadCount,
    List<NotificationResponse> Items);

public sealed record UnreadCountResponse(int UnreadCount);

public sealed class RegisterDeviceRequest
{
    [Required(ErrorMessage = "Thiếu FCM token của thiết bị.")]
    [MaxLength(4096)]
    public string FcmToken { get; init; } = string.Empty;

    /// <summary>ANDROID | IOS | WEB</summary>
    [MaxLength(20)]
    public string Platform { get; init; } = "ANDROID";

    [MaxLength(255)]
    public string? DeviceId { get; init; }
}

public sealed class UnregisterDeviceRequest
{
    [Required(ErrorMessage = "Thiếu FCM token cần gỡ.")]
    [MaxLength(4096)]
    public string FcmToken { get; init; } = string.Empty;
}

public sealed class TestNotificationRequest
{
    [MaxLength(255)]
    public string? Title { get; init; }

    [MaxLength(1000)]
    public string? Content { get; init; }
}
