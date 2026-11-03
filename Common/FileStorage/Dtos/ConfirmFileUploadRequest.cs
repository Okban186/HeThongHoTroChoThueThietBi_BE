namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;

public sealed class ConfirmFileUploadRequest
{
    public List<Guid> SessionIds { get; set; } = [];
}