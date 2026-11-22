using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

public sealed class CancelReservationRequest
{
    // Lý do hủy (ví dụ: khách không thuê nữa), được ghi thêm vào ghi chú của phiếu.
    [MaxLength(500, ErrorMessage = "Lý do tối đa 500 ký tự.")]
    public string? Reason { get; init; }
}
