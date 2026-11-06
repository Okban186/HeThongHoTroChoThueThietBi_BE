namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

public sealed class FileUploadTicket
{
    public string CloudName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ResourceType { get; set; } = string.Empty;

    public int MaxFileCount { get; set; }

    public long MaxFileSizeBytes { get; set; }

    public string[] AllowedExtensions { get; set; } = [];

    public List<CloudinaryUploadItem> Uploads { get; set; } = [];
}

public sealed class CloudinaryUploadItem
{
    public Guid SessionId { get; set; }
    public string UploadUrl { get; set; } = string.Empty;

    public List<string> AllowedFormats { get; set; } = [];

    public string Folder { get; set; } = string.Empty;

    public string OverWrite { get; set; } = "false";

    // public_id đầy đủ trên Cloudinary
    public string PublicId { get; set; } = string.Empty;

    public long Timestamp { get; set; }

    public string Signature { get; set; } = string.Empty;

    public string Type { get; set; } = "authenticated";
}