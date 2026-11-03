namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

public sealed class FileUploadPolicy
{
    // Số lượng file tối đa trong một lần upload
    public int MaxFileCount { get; init; }

    // Dung lượng tối đa của một file
    public long MaxFileSizeBytes { get; init; }

    // Các extension được phép
    public string[] AllowedExtensions { get; init; } = [];

    // Loại tài nguyên Cloudinary
    public CloudinaryResourceType ResourceType { get; init; }

    // Folder lấy từ enum dùng chung
    public StorageFolder Folder { get; init; }
}