using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;

/// <summary>
/// Kiểm tra số lượng khả dụng của nhiều model cùng lúc (ví dụ màn chi tiết phiếu giữ chỗ).
/// </summary>
public sealed class CheckEquipmentAvailabilityRequest
{
    [Required, MinLength(1), MaxLength(100)]
    public List<Guid> EquipmentModelIds { get; init; } = [];

    [Required]
    public DateOnly? From { get; init; }

    [Required]
    public DateOnly? To { get; init; }

    // Phiếu đang chỉnh sửa, bỏ qua số lượng của chính phiếu này.
    public Guid? ExcludeReservationId { get; init; }
}
