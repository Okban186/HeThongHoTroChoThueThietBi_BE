using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Notification.DTOs;
using HeThongHoTroChoThueThietBi.Features.Notification.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Notification.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationController(NotificationService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationListResponse>> GetMyNotifications(
        [FromQuery] int limit = 50,
        [FromQuery] bool unreadOnly = false,
        CancellationToken ct = default)
        => Ok(await service.GetMyNotificationsAsync(CurrentUserId(), limit, unreadOnly, ct));

    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountResponse>> GetUnreadCount(CancellationToken ct)
        => Ok(await service.GetUnreadCountAsync(CurrentUserId(), ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken ct)
    {
        await service.MarkAsReadAsync(id, CurrentUserId(), ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<object>> MarkAllAsRead(CancellationToken ct)
    {
        var updated = await service.MarkAllAsReadAsync(CurrentUserId(), ct);
        return Ok(new { updated });
    }

    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken ct)
    {
        await service.RegisterDeviceAsync(CurrentUserId(), request, ct);
        return NoContent();
    }

    [HttpPost("devices/unregister")]
    public async Task<IActionResult> UnregisterDevice(
        [FromBody] UnregisterDeviceRequest request,
        CancellationToken ct)
    {
        await service.UnregisterDeviceAsync(CurrentUserId(), request.FcmToken, ct);
        return NoContent();
    }

    /// <summary>Gửi thử 1 thông báo đẩy + In-App tới chính tài khoản đang đăng nhập để kiểm tra cấu hình Firebase.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTest(
        [FromBody] TestNotificationRequest? request,
        CancellationToken ct)
    {
        await service.SendTestNotificationAsync(CurrentUserId(), request, ct);
        return Ok(new { message = "Đã gửi thông báo kiểm tra (In-App + FCM Push)." });
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
