namespace HeThongHoTroChoThueThietBi.Common.Notifications.Configurations;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; } = false;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Hệ Thống Cho Thuê Thiết Bị";
}
