using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>
/// Thay toàn bộ danh sách thiết bị (model + số lượng) của phiếu.
/// Cùng một model xuất hiện nhiều dòng sẽ được gộp số lượng.
/// </summary>
public sealed class UpdateReservationItemsRequest
{
    [Required]
    [MaxLength(50, ErrorMessage = "Tối đa 50 dòng thiết bị.")]
    public List<ReservationItemRequest> Items { get; init; } = [];
}

public sealed class ReservationItemRequest
{
    [Required(ErrorMessage = "Vui lòng chọn model thiết bị.")]
    public Guid? EquipmentModelId { get; init; }

    [Range(1, 9999, ErrorMessage = "Số lượng từ 1 đến 9999.")]
    public int Quantity { get; init; }

    [MaxLength(500, ErrorMessage = "Ghi chú tối đa 500 ký tự.")]
    public string? Note { get; init; }
}
