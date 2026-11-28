using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Features.Notification.DTOs;
using HeThongHoTroChoThueThietBi.Features.Notifications;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Notification.Services;

public sealed class NotificationService(
    AppDbContext db,
    INotificationDispatcher dispatcher)
{
    public async Task<NotificationListResponse> GetMyNotificationsAsync(
        Guid userId,
        int limit = 50,
        bool unreadOnly = false,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        var unreadCount = await db.Notifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && !n.IsRead, ct);

        var query = db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId);

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(safeLimit)
            .Select(n => new NotificationResponse(
                n.Id,
                n.UserId,
                n.Title,
                n.Content,
                n.Type,
                n.IsRead,
                n.ReadAt,
                n.CreatedAt))
            .ToListAsync(ct);

        return new NotificationListResponse(unreadCount, items);
    }

    public async Task<UnreadCountResponse> GetUnreadCountAsync(Guid userId, CancellationToken ct = default)
    {
        var count = await db.Notifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && !n.IsRead, ct);
        return new UnreadCountResponse(count);
    }

    public async Task MarkAsReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default)
    {
        var entity = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct)
            ?? throw new AppException("NOTIFICATION_NOT_FOUND", "Không tìm thấy thông báo.", StatusCodes.Status404NotFound);

        if (!entity.IsRead)
        {
            entity.IsRead = true;
            entity.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.IsRead, true)
                .SetProperty(n => n.ReadAt, now), ct);
    }

    /// <summary>Đăng ký / cập nhật FCM token cho tài khoản đang đăng nhập (tự gán lại nếu đổi tài khoản trên cùng máy).</summary>
    public async Task RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var token = request.FcmToken.Trim();
        var platform = string.IsNullOrWhiteSpace(request.Platform)
            ? "ANDROID"
            : request.Platform.Trim().ToUpperInvariant();
        if (platform.Length > 20) platform = platform[..20];

        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();
        var nowUtc = DateTime.UtcNow;

        var existing = await db.UserDevices
            .FirstOrDefaultAsync(d => d.FcmToken == token, ct);

        if (existing is not null)
        {
            existing.UserId = userId;
            existing.Platform = platform;
            existing.DeviceId = deviceId ?? existing.DeviceId;
            existing.UpdatedAt = nowUtc;
        }
        else
        {
            db.UserDevices.Add(new UserDevice
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                FcmToken = token,
                DeviceId = deviceId,
                Platform = platform,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UnregisterDeviceAsync(Guid userId, string fcmToken, CancellationToken ct = default)
    {
        var token = fcmToken.Trim();
        if (string.IsNullOrEmpty(token)) return;

        await db.UserDevices
            .Where(d => d.UserId == userId && d.FcmToken == token)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Gửi thử 1 thông báo (In-App + FCM Push) tới chính người đang đăng nhập để kiểm tra cấu hình Firebase.</summary>
    public async Task SendTestNotificationAsync(Guid userId, TestNotificationRequest? request, CancellationToken ct = default)
    {
        var title = string.IsNullOrWhiteSpace(request?.Title)
            ? "Kiểm tra thông báo EquipRent"
            : request!.Title!.Trim();
        var content = string.IsNullOrWhiteSpace(request?.Content)
            ? $"Kết nối Firebase Cloud Messaging hoạt động thành công ({DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)):HH:mm:ss dd/MM/yyyy})."
            : request!.Content!.Trim();

        await dispatcher.NotifyUsersAsync([userId], title, content, "TEST_NOTIFICATION", cancellationToken: ct);
    }
}
