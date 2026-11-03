using System.Net;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Configurations;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Entities;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Services;

public class CloudinaryUploadService : IFileUploadService
{
    private readonly Cloudinary _cloudinary;
    private readonly CloudinaryOptions _options;
    private readonly AppDbContext _dbContext;

    public CloudinaryUploadService(Cloudinary cloudinary, IOptions<CloudinaryOptions> options, AppDbContext dbContext)
    {
        _cloudinary = cloudinary;
        _options = options.Value;
        _dbContext = dbContext;
    }

    public async Task<FileUploadTicket> CreateUploadTicketAsync(FileUploadPolicy policy, FileUploadRequest request, Guid uploadedBy, CancellationToken cancellationToken = default)
    {
        // Kiểm tra policy
        if (policy.MaxFileCount <= 0 || policy.MaxFileSizeBytes <= 0 || policy.AllowedExtensions is null || policy.AllowedExtensions.Length == 0)
        {
            throw new AppException("UPLOAD_POLICY_INVALID", "Cấu hình upload không hợp lệ.", StatusCodes.Status500InternalServerError);
        }

        if (!Enum.IsDefined(policy.Folder) ||
            !Enum.IsDefined(policy.ResourceType))
        {
            throw new AppException("UPLOAD_POLICY_INVALID", "Thư mục hoặc loại tài nguyên không hợp lệ.", StatusCodes.Status500InternalServerError);
        }

        if (request.FileCount <= 0 ||
            request.FileCount > policy.MaxFileCount)
        {
            throw new AppException("UPLOAD_FILE_COUNT_INVALID", "Số lượng file upload không hợp lệ.");
        }

        var folder = policy.Folder.ToFolderName();

        var resourceType = policy.ResourceType.ToString().ToLowerInvariant();

        var now = DateTimeOffset.UtcNow;

        var uploads = new List<CloudinaryUploadItem>();
        var sessions = new List<UploadSession>();

        var cleanExtensions = policy.AllowedExtensions.Select(ext => ext.Replace(".", "").Trim().ToLower());

        var allowedFormatsString = string.Join(",", cleanExtensions);

        for (var i = 0; i < request.FileCount; i++)
        {
            // Mỗi file có một UUID/session riêng
            var sessionId = Guid.CreateVersion7();

            // UUID session cũng chính là UUID trong public_id
            var publicId = $"{sessionId:N}";

            var timestamp = now.ToUnixTimeSeconds();

            var parameters = new Dictionary<string, object>
            {
                ["allowed_formats"] = allowedFormatsString,
                ["folder"] = "temp",
                ["overwrite"] = "false",
                ["public_id"] = publicId,
                ["timestamp"] = timestamp

            };

            var signature = _cloudinary.Api.SignParameters(parameters);

            // Tạo session lưu policy tại thời điểm cấp ticket
            var session = new UploadSession
            {
                Id = sessionId,
                UploadedBy = uploadedBy,
                Folder = policy.Folder,
                ResourceType = policy.ResourceType,
                MaxFileSizeBytes = policy.MaxFileSizeBytes,
                AllowedExtensions = [.. policy.AllowedExtensions],
                ExpiresAt = now.AddMinutes(30),
                Status = UploadSessionStatus.Pending,
                CreatedAt = now
            };

            sessions.Add(session);

            var uploadUrl = $"https://api.cloudinary.com/v1_1/{_options.CloudName}/{resourceType}/upload";

            uploads.Add(new CloudinaryUploadItem
            {
                SessionId = sessionId,
                UploadUrl = uploadUrl,
                PublicId = publicId,
                Timestamp = timestamp,
                Signature = signature
            });
        }

        // Lưu tất cả session trước khi trả ticket
        await _dbContext.UploadSessions.AddRangeAsync(sessions, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new FileUploadTicket
        {
            CloudName = _options.CloudName,
            ApiKey = _options.ApiKey,
            ResourceType = resourceType,
            MaxFileCount = policy.MaxFileCount,
            MaxFileSizeBytes = policy.MaxFileSizeBytes,
            AllowedExtensions = policy.AllowedExtensions,
            Uploads = uploads
        };
    }

    public async Task<ConfirmFileUploadResponse> ConfirmUploadAsync(ConfirmFileUploadRequest request, Guid uploadedBy, CancellationToken cancellationToken = default)
    {
        if (request.SessionIds is null || request.SessionIds.Count == 0)
        {
            throw new AppException("UPLOAD_SESSION_IDS_REQUIRED", "Danh sách phiên upload không được để trống.");
        }

        var results = new List<ConfirmFileUploadResult>();

        foreach (var sessionId in request.SessionIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ConfirmSingleUploadAsync(sessionId, uploadedBy, cancellationToken));
        }

        return new ConfirmFileUploadResponse { Results = results };
    }

    private async Task<ConfirmFileUploadResult> ConfirmSingleUploadAsync(Guid sessionId, Guid uploadedBy, CancellationToken cancellationToken)
    {
        var session = await _dbContext.UploadSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

        if (session is null)
            return Failed(sessionId, "UPLOAD_SESSION_NOT_FOUND", "Không tìm thấy phiên upload.");

        if (session.UploadedBy != uploadedBy)
            return Failed(sessionId, "UPLOAD_SESSION_FORBIDDEN", "Bạn không có quyền xác nhận file này.");

        var folder = session.Folder.ToFolderName();
        var objectKey = session.Id.ToString("N");
        var resourceType = ConvertResourceType(session.ResourceType);

        var existingFile = await _dbContext.StorageFiles.FirstOrDefaultAsync(
            x => x.BucketName == folder && x.ObjectKey == objectKey,
            cancellationToken);

        if (existingFile is not null)
        {
            if (session.Status != UploadSessionStatus.Completed)
            {
                session.Status = UploadSessionStatus.Completed;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return Succeeded(sessionId, existingFile.Id);
        }

        if (session.Status == UploadSessionStatus.Completed)
        {
            return Failed(
                sessionId,
                "UPLOAD_STORAGE_RECORD_NOT_FOUND",
                "Phiên upload đã hoàn tất nhưng không tìm thấy bản ghi file.");
        }

        if (session.Status == UploadSessionStatus.Expired ||
            session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            session.Status = UploadSessionStatus.Expired;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return Failed(sessionId, "UPLOAD_SESSION_EXPIRED", "Phiên upload đã hết hạn.");
        }

        var temporaryPublicId = $"temp/{objectKey}";
        var finalPublicId = $"{folder}/{objectKey}";

        // CloudinaryDotNet trả lỗi API trong result.Error. Không dùng catch
        // để phân biệt file không tồn tại, vì 404 thường không được throw.
        var resource = await GetResourceAsync(temporaryPublicId, resourceType, cancellationToken);

        var currentPublicId = temporaryPublicId;

        if (resource.Error is not null)
        {
            if (resource.StatusCode != HttpStatusCode.NotFound)
            {
                return Failed(sessionId, "UPLOAD_CLOUDINARY_LOOKUP_FAILED", "Không thể kiểm tra file trên Cloudinary.");
            }

            // Tương thích với lần Confirm trước đã Rename thành công,
            // nhưng asset_folder vẫn còn ở temp hoặc DB chưa lưu thành công.
            resource = await GetResourceAsync(finalPublicId, resourceType, cancellationToken);

            currentPublicId = finalPublicId;

            if (resource.Error is not null)
            {
                if (resource.StatusCode == HttpStatusCode.NotFound)
                {
                    return Failed(
                        sessionId,
                        "UPLOAD_FILE_NOT_FOUND",
                        "Không tìm thấy file trên Cloudinary.");
                }

                return Failed(
                    sessionId,
                    "UPLOAD_CLOUDINARY_LOOKUP_FAILED",
                    "Không thể kiểm tra file trên Cloudinary.");
            }
        }

        if (resource.PublicId != currentPublicId)
        {
            return Failed(
                sessionId,
                "UPLOAD_FILE_INVALID",
                "Public ID của file trên Cloudinary không khớp với phiên upload.");
        }

        if (resource.Bytes > session.MaxFileSizeBytes)
        {
            return Failed(
                sessionId,
                "UPLOAD_FILE_SIZE_INVALID",
                "Dung lượng file vượt quá giới hạn cho phép.");
        }

        // đổi Public ID để thống nhất với BucketName/ObjectKey.
        // Rename KHÔNG tự di chuyển asset_folder trong Dynamic Folder Mode.
        if (currentPublicId != finalPublicId)
        {
            var renameResult = await _cloudinary.RenameAsync(
                new RenameParams(currentPublicId, finalPublicId)
                {
                    ResourceType = resourceType,
                    Overwrite = false
                },
                cancellationToken);

            if (renameResult.Error is not null)
            {
                // Nếu lần gọi trước đã rename thành công nhưng response bị mất,
                // kiểm tra Public ID đích để retry không bị kẹt.
                var finalResource = await GetResourceAsync(
                    finalPublicId,
                    resourceType,
                    cancellationToken);

                if (finalResource.Error is not null)
                {
                    return Failed(
                        sessionId,
                        "UPLOAD_RENAME_FAILED",
                        "Không thể cập nhật Public ID của file trên Cloudinary.");
                }
            }

            currentPublicId = finalPublicId;
        }

        // di chuyển asset thật sự vào folder đích trong Media Library.
        // AssetFolder chỉ dùng được với Dynamic Folder Mode.
        var moveResult = await _cloudinary.ExplicitAsync(
            new ExplicitParams(currentPublicId)
            {
                ResourceType = resourceType,
                Type = "upload",
                AssetFolder = folder
            },
            cancellationToken);

        if (moveResult.Error is not null)
        {
            return Failed(
                sessionId,
                "UPLOAD_MOVE_FAILED",
                "Không thể di chuyển file vào thư mục lưu trữ trên Cloudinary.");
        }

        var originalName = resource.OriginalFilename;

        if (string.IsNullOrWhiteSpace(originalName))
        {
            var format = resource.Format;
            originalName = string.IsNullOrWhiteSpace(format)
                ? objectKey
                : $"{objectKey}.{format}";
        }

        originalName = Path.GetFileName(originalName);

        if (string.IsNullOrWhiteSpace(originalName))
            originalName = objectKey;

        if (originalName.Length > 255)
            originalName = originalName[^255..];

        var storageFile = new StorageFile
        {
            Id = Guid.CreateVersion7(),
            ObjectKey = objectKey,
            BucketName = folder,
            OriginalName = originalName,
            SizeBytes = resource.Bytes,
            UploadedBy = uploadedBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.StorageFiles.Add(storageFile);
        session.Status = UploadSessionStatus.Completed;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Succeeded(sessionId, storageFile.Id);
    }

    private Task<GetResourceResult> GetResourceAsync(
        string publicId,
        ResourceType resourceType,
        CancellationToken cancellationToken)
    {
        return _cloudinary.GetResourceAsync(
            new GetResourceParams(publicId)
            {
                ResourceType = resourceType
            },
            cancellationToken);
    }

    private static ResourceType ConvertResourceType(CloudinaryResourceType resourceType)
    {
        return resourceType switch
        {
            CloudinaryResourceType.Image => ResourceType.Image,
            CloudinaryResourceType.Video => ResourceType.Video,
            CloudinaryResourceType.Raw => ResourceType.Raw,
            _ => throw new InvalidOperationException("Cloudinary resource type không hợp lệ.")
        };
    }

    private static ConfirmFileUploadResult Succeeded(Guid sessionId, Guid storageFileId)
    {
        return new ConfirmFileUploadResult
        {
            SessionId = sessionId,
            StorageFileId = storageFileId,
            Success = true
        };
    }

    private static ConfirmFileUploadResult Failed(Guid sessionId, string errorCode, string message)
    {
        return new ConfirmFileUploadResult
        {
            SessionId = sessionId,
            StorageFileId = null,
            Success = false,
            ErrorCode = errorCode,
            Message = message
        };
    }
}
