using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>
/// Lập phiếu giữ chỗ. Mã phiếu (PH-YYYY-NNNN) và hạn giữ chỗ do hệ thống tạo.
/// Thiết bị được thêm sau ở màn chi tiết (PUT /api/reservations/{id}/items).
/// </summary>
public sealed class CreateReservationRequest
{
    [Required(ErrorMessage = "Vui lòng chọn công ty.")]
    public Guid? CompanyId { get; init; }

    [Required(ErrorMessage = "Vui lòng chọn ngày bắt đầu.")]
    public DateOnly? RequestedFrom { get; init; }

    [Required(ErrorMessage = "Vui lòng chọn ngày kết thúc.")]
    public DateOnly? RequestedTo { get; init; }

    [Required(ErrorMessage = "Vui lòng nhập người liên hệ.")]
    [MaxLength(150, ErrorMessage = "Người liên hệ tối đa 150 ký tự.")]
    public string ContactName { get; init; } = string.Empty;

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại.")]
    [RegularExpression("^[0-9+ ]{8,20}$", ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string ContactPhone { get; init; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "Ghi chú tối đa 2000 ký tự.")]
    public string? Note { get; init; }
}
