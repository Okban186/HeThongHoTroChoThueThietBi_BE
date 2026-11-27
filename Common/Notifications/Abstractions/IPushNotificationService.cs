namespace HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;

public interface IPushNotificationService
{
    /// <summary>
    /// Đẩy Push Notification qua Firebase Cloud Messaging tới toàn bộ thiết bị đang hoạt động của các user nhận.
    /// Key = UserId, Value = NotificationId vừa lưu trong bảng notifications.
    /// </summary>
    Task SendToUsersAsync(
        IReadOnlyDictionary<Guid, Guid> notificationIdsByUser,
        string title,
        string content,
        string type,
        CancellationToken cancellationToken = default);
}
