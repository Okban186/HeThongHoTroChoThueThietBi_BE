using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Configurations;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Common.Notifications.Services;

public sealed class FirebasePushNotificationService(
    IOptions<FirebaseOptions> options,
    IServiceScopeFactory scopeFactory,
    IHostEnvironment hostEnvironment,
    ILogger<FirebasePushNotificationService> logger) : IPushNotificationService
{
    private const string AndroidChannelId = "equiprent_notifications";
    private const int MaxBatchSize = 500;

    private readonly FirebaseOptions _opt = options.Value;
    private readonly object _initLock = new();
    private bool _initAttempted;
    private bool _isReady;

    public async Task SendToUsersAsync(
        IReadOnlyDictionary<Guid, Guid> notificationIdsByUser,
        string title,
        string content,
        string type,
        CancellationToken cancellationToken = default)
    {
        if (notificationIdsByUser.Count == 0)
            return;

        if (!EnsureInitialized())
            return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var userIds = notificationIdsByUser.Keys.ToList();
        var devices = await db.UserDevices.AsNoTracking()
            .Where(d => userIds.Contains(d.UserId) && !string.IsNullOrWhiteSpace(d.FcmToken))
            .Select(d => new { d.Id, d.UserId, d.FcmToken })
            .ToListAsync(cancellationToken);

        if (devices.Count == 0)
            return;

        var invalidDeviceIds = new List<Guid>();

        for (var offset = 0; offset < devices.Count; offset += MaxBatchSize)
        {
            var chunk = devices.Skip(offset).Take(MaxBatchSize).ToList();
            var messages = new List<Message>(chunk.Count);

            foreach (var d in chunk)
            {
                var notifId = notificationIdsByUser[d.UserId];
                messages.Add(new Message
                {
                    Token = d.FcmToken,
                    Notification = new FirebaseAdmin.Messaging.Notification
                    {
                        Title = title,
                        Body = content,
                    },
                    Android = new AndroidConfig
                    {
                        Priority = Priority.High,
                        Notification = new AndroidNotification
                        {
                            ChannelId = AndroidChannelId,
                            Sound = "default",
                        },
                    },
                    Apns = new ApnsConfig
                    {
                        Aps = new Aps
                        {
                            Sound = "default",
                        },
                    },
                    Data = new Dictionary<string, string>
                    {
                        ["notificationId"] = notifId.ToString(),
                        ["userId"] = d.UserId.ToString(),
                        ["type"] = type ?? string.Empty,
                        ["title"] = title,
                        ["body"] = content,
                    },
                });
            }

            try
            {
                var response = await FirebaseMessaging.DefaultInstance.SendEachAsync(messages, cancellationToken);

                for (var i = 0; i < response.Responses.Count; i++)
                {
                    var res = response.Responses[i];
                    if (res.IsSuccess)
                        continue;

                    var errCode = res.Exception?.MessagingErrorCode;
                    if (errCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                    {
                        invalidDeviceIds.Add(chunk[i].Id);
                    }
                    else
                    {
                        logger.LogWarning(
                            res.Exception,
                            "Gửi FCM thất bại tới device {DeviceId} (errorCode={ErrorCode}).",
                            chunk[i].Id, errCode);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lỗi gọi FirebaseMessaging.SendEachAsync cho thông báo '{Title}'.", title);
            }
        }

        // Tự động dọn các FCM token đã hết hạn / đã gỡ app khỏi user_devices
        if (invalidDeviceIds.Count > 0)
        {
            try
            {
                await db.UserDevices
                    .Where(d => invalidDeviceIds.Contains(d.Id))
                    .ExecuteDeleteAsync(cancellationToken);
                logger.LogInformation("Đã dọn {Count} FCM token hết hạn/không hợp lệ.", invalidDeviceIds.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không dọn được {Count} FCM token hết hạn.", invalidDeviceIds.Count);
            }
        }
    }

    private bool EnsureInitialized()
    {
        if (!_opt.Enabled)
            return false;

        if (_isReady)
            return true;

        lock (_initLock)
        {
            if (_isReady)
                return true;

            if (FirebaseApp.DefaultInstance is not null)
            {
                _isReady = true;
                return true;
            }

            if (_initAttempted)
                return false;

            _initAttempted = true;

            try
            {
                GoogleCredential? credential = null;

                if (!string.IsNullOrWhiteSpace(_opt.CredentialJson))
                {
                    credential = GoogleCredential.FromJson(_opt.CredentialJson);
                }
                else if (!string.IsNullOrWhiteSpace(_opt.CredentialPath))
                {
                    var resolvedPath = Path.IsPathRooted(_opt.CredentialPath)
                        ? _opt.CredentialPath
                        : Path.Combine(hostEnvironment.ContentRootPath, _opt.CredentialPath);

                    if (!File.Exists(resolvedPath))
                    {
                        logger.LogWarning(
                            "[Firebase] Không tìm thấy file Service Account tại '{Path}'. Tạm bỏ qua FCM Push (In-App Notification vẫn hoạt động bình thường).",
                            resolvedPath);
                        return false;
                    }

                    credential = GoogleCredential.FromFile(resolvedPath);
                }

                if (credential is null)
                {
                    logger.LogWarning("[Firebase] Chưa cấu hình CredentialPath hoặc CredentialJson.");
                    return false;
                }

                FirebaseApp.Create(new AppOptions
                {
                    Credential = credential,
                });

                _isReady = true;
                logger.LogInformation("[Firebase] Đã khởi tạo FirebaseApp thành công — sẵn sàng gửi FCM Push Notification.");
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Firebase] Khởi tạo FirebaseAdmin thất bại.");
                return false;
            }
        }
    }
}
