namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;

public sealed class ConfirmFileUploadResponse
{
    public List<ConfirmFileUploadResult> Results { get; set; } = [];
}

public sealed class ConfirmFileUploadResult
{
    public Guid SessionId { get; set; }

    public Guid? StorageFileId { get; set; }

    public bool Success { get; set; }

    public string? ErrorCode { get; set; }

    public string? Message { get; set; }
}