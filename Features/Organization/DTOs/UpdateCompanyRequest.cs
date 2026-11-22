using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Organization.DTOs;

public sealed class UpdateCompanyRequest
{
    [Required(ErrorMessage = "Vui lòng nhập tên công ty.")]
    [MaxLength(255, ErrorMessage = "Tên công ty tối đa 255 ký tự.")]
    public string Name { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập mã số thuế.")]
    [RegularExpression(@"^\d{10}(-\d{3})?$", ErrorMessage = "Mã số thuế gồm 10 chữ số (hoặc 10 số kèm -XXX cho chi nhánh).")]
    public string TaxCode { get; init; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Địa chỉ tối đa 500 ký tự.")]
    public string? Address { get; init; }
}
