namespace HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;

/// <summary>
/// Dịch vụ gửi thông báo dùng chung toàn hệ thống (In-App Notification + Email HTML).
/// Tái sử dụng được cho khoản phạt, thanh toán, hợp đồng, sự cố, lịch giao nhận...
/// </summary>
public interface INotificationDispatcher
{
    Task NotifyUsersAsync(
        IEnumerable<Guid> userIds,
        string title,
        string content,
        string type,
        string? emailSubject = null,
        string? emailHtmlBody = null,
        CancellationToken cancellationToken = default);

    Task NotifyCompanyAsync(
        Guid companyId,
        string title,
        string content,
        string type,
        IEnumerable<string>? extraEmails = null,
        string? emailSubject = null,
        string? emailHtmlBody = null,
        CancellationToken cancellationToken = default);
}
