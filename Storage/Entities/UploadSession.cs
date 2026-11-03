using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Entities;

[Table("upload_sessions")]
public class UploadSession
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("uploaded_by")]
    public Guid UploadedBy { get; set; }

    [Column("folder")]
    [MaxLength(50)]
    [Required]
    public StorageFolder Folder { get; set; }

    [Column("resource_type")]
    [MaxLength(20)]
    [Required]
    public CloudinaryResourceType ResourceType { get; set; }

    [Column("max_file_size_bytes")]
    public long MaxFileSizeBytes { get; set; }

    [Column("allowed_extensions", TypeName = "text[]")]
    public string[] AllowedExtensions { get; set; } = [];

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("status")]
    [Required]
    public UploadSessionStatus Status { get; set; } = UploadSessionStatus.Pending;

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }
}