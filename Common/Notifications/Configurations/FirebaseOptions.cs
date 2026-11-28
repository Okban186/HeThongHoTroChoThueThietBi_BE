namespace HeThongHoTroChoThueThietBi.Common.Notifications.Configurations;

public sealed class FirebaseOptions
{
    public const string SectionName = "Firebase";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Đường dẫn tới file JSON Service Account tải từ Firebase Console
    /// (tương đối theo thư mục gốc Backend hoặc đường dẫn tuyệt đối).
    /// </summary>
    public string CredentialPath { get; init; } = "firebase-service-account.json";

    /// <summary>Tùy chọn truyền trực tiếp chuỗi JSON Service Account qua biến môi trường.</summary>
    public string? CredentialJson { get; init; }
}
