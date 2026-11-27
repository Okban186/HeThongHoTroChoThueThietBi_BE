using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>Manager từ chối hợp đồng (bắt buộc lý do để Sales chỉnh sửa).</summary>
public sealed class RejectRentalContractRequest
{
    [Required(ErrorMessage = "Vui lòng nhập lý do từ chối.")]
    [MaxLength(1000, ErrorMessage = "Lý do tối đa 1000 ký tự.")]
    public string Reason { get; init; } = string.Empty;
}

public sealed class CancelRentalContractRequest
{
    [MaxLength(1000, ErrorMessage = "Lý do tối đa 1000 ký tự.")]
    public string? Reason { get; init; }

    /// <summary>Phí phạt hủy hợp đồng trước hạn (nếu có).</summary>
    [Range(0, double.MaxValue)]
    public decimal? PenaltyAmount { get; init; }

    /// <summary>Phí vận chuyển thực tế phải thu khi khách hủy (thu hồi ưu đãi miễn phí vận chuyển nếu có).</summary>
    [Range(0, double.MaxValue)]
    public decimal? TransportFeeAmount { get; init; }
}

/// <summary>Sales tải lên bản hợp đồng đã ký (PENDING_SIGNATURE → SIGNED).</summary>
public sealed class MarkRentalContractSignedRequest
{
    [Required(ErrorMessage = "Vui lòng tải lên bản hợp đồng đã ký.")]
    public Guid? ContractFileId { get; init; }
}
