namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Configurations;

public sealed class CloudinaryOptions
{
    public string CloudName { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ApiSecret { get; set; } = string.Empty;

    public Dictionary<string, string> UploadPresets { get; set; } = new();
}