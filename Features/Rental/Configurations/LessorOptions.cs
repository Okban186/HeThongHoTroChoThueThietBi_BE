namespace HeThongHoTroChoThueThietBi.Features.Rental.Configurations;

/// <summary>
/// Thông tin bên cho thuê (Bên A) in trên hợp đồng PDF (section "Lessor" trong appsettings).
/// DB chưa có bảng thông tin doanh nghiệp nên để ở cấu hình; đổi công ty thì chỉ sửa appsettings.
/// </summary>
public sealed class LessorOptions
{
    public const string SectionName = "Lessor";

    public string CompanyName { get; set; } = "";
    public string TaxCode { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankName { get; set; }

    /// <summary>Người đại diện ký hợp đồng và chức vụ.</summary>
    public string RepresentativeName { get; set; } = "";
    public string RepresentativePosition { get; set; } = "Giám đốc";

    /// <summary>Nơi ký hợp đồng, in ở dòng "…, ngày … tháng … năm …".</summary>
    public string SigningPlace { get; set; } = "TP. Hồ Chí Minh";
}
