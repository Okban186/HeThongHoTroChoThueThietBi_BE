using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

namespace HeThongHoTroChoThueThietBi.Storage.Entities;

[Table("storage_files")]
public class StorageFile
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("object_key")]
    [Required]
    public string ObjectKey { get; set; } = null!;

    [Column("bucket_name")]
    [MaxLength(100)]
    [Required]
    public string BucketName { get; set; } = null!;

    [Column("original_name")]
    [MaxLength(255)]
    [Required]
    public string OriginalName { get; set; } = null!;

    [Column("resource_type")]
    [Required]
    public CloudinaryResourceType ResourceType { get; set; }

    [Column("content_type")]
    [MaxLength(150)]
    public string? ContentType { get; set; }

    [Column("size_bytes")]
    public long SizeBytes { get; set; }

    [Column("metadata", TypeName = "jsonb")]
    public string? Metadata { get; set; }

    [Column("uploaded_by")]
    public Guid? UploadedBy { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
