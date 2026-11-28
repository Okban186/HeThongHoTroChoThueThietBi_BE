using System.Net;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Features.Identity.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Common.Notifications.Services;

public sealed class NotificationDispatcher(
    AppDbContext db,
    IEmailService emailService,
    IPushNotificationService pushNotificationService,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public async Task NotifyUsersAsync(
        IEnumerable<Guid> userIds,
        string title,
        string content,
        string type,
        string? emailSubject = null,
        string? emailHtmlBody = null,
        CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
            return;

        var users = await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active)
            .Select(u => new { u.Id, u.Email })
            .ToListAsync(cancellationToken);

        if (users.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var notificationIdsByUser = new Dictionary<Guid, Guid>(users.Count);

        foreach (var u in users)
        {
            var notifId = Guid.CreateVersion7();
            notificationIdsByUser[u.Id] = notifId;
            db.Notifications.Add(new Features.Notification.Entities.Notification
            {
                Id = notifId,
                UserId = u.Id,
                Title = title,
                Content = content,
                Type = type,
                IsRead = false,
                CreatedAt = now,
            });
        }

        // Lưu ngay xuống PostgreSQL (kể cả khi caller gọi sau CommitAsync)
        await db.SaveChangesAsync(cancellationToken);

        // Đẩy FCM Push Notification (không làm vỡ nghiệp vụ chính nếu mạng/Firebase gặp sự cố)
        try
        {
            await pushNotificationService.SendToUsersAsync(
                notificationIdsByUser, title, content, type, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gửi FCM Push cho thông báo '{Title}' thất bại.", title);
        }

        if (!string.IsNullOrWhiteSpace(emailSubject) || !string.IsNullOrWhiteSpace(emailHtmlBody))
        {
            var emails = users
                .Where(u => !string.IsNullOrWhiteSpace(u.Email))
                .Select(u => u.Email!)
                .ToList();

            await emailService.SendHtmlAsync(
                emails,
                emailSubject ?? title,
                emailHtmlBody ?? BuildDefaultHtml(title, content),
                cancellationToken);
        }
    }

    public async Task NotifyCompanyAsync(
        Guid companyId,
        string title,
        string content,
        string type,
        IEnumerable<string>? extraEmails = null,
        string? emailSubject = null,
        string? emailHtmlBody = null,
        CancellationToken cancellationToken = default)
    {
        var companyUsers = await (
            from cu in db.CompanyUsers.AsNoTracking()
            join u in db.Users.AsNoTracking() on cu.UserId equals u.Id
            where cu.CompanyId == companyId && u.Status == UserStatus.Active
            select new { u.Id, u.Email }
        ).ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var notificationIdsByUser = new Dictionary<Guid, Guid>(companyUsers.Count);

        foreach (var u in companyUsers)
        {
            if (notificationIdsByUser.ContainsKey(u.Id))
                continue;

            var notifId = Guid.CreateVersion7();
            notificationIdsByUser[u.Id] = notifId;
            db.Notifications.Add(new Features.Notification.Entities.Notification
            {
                Id = notifId,
                UserId = u.Id,
                Title = title,
                Content = content,
                Type = type,
                IsRead = false,
                CreatedAt = now,
            });
        }

        if (notificationIdsByUser.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                await pushNotificationService.SendToUsersAsync(
                    notificationIdsByUser, title, content, type, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gửi FCM Push tới công ty {CompanyId} cho thông báo '{Title}' thất bại.", companyId, title);
            }
        }

        var allEmails = companyUsers
            .Where(u => !string.IsNullOrWhiteSpace(u.Email))
            .Select(u => u.Email!)
            .Concat(extraEmails ?? [])
            .ToList();

        if (allEmails.Count > 0)
        {
            await emailService.SendHtmlAsync(
                allEmails,
                emailSubject ?? title,
                emailHtmlBody ?? BuildDefaultHtml(title, content),
                cancellationToken);
        }
    }

    public static string BuildDefaultHtml(string title, string content)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeBody = WebUtility.HtmlEncode(content).Replace("\n", "<br/>");
        return $"""
        <div style="font-family:Segoe UI,Arial,sans-serif;max-width:600px;margin:auto;border:1px solid #e2e8f0;border-radius:12px;overflow:hidden">
          <div style="background:#1e40af;color:#fff;padding:16px 20px;font-size:16px;font-weight:700">
            {safeTitle}
          </div>
          <div style="padding:20px;color:#1e293b;line-height:1.6;font-size:14px">
            {safeBody}
          </div>
          <div style="background:#f8fafc;color:#64748b;padding:12px 20px;font-size:12px">
            Thông báo tự động từ Hệ thống Quản lý Cho thuê Thiết bị.
          </div>
        </div>
        """;
    }
}
