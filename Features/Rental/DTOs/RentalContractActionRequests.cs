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
}

/// <summary>Sales tải lên bản hợp đồng đã ký (PENDING_SIGNATURE → SIGNED).</summary>
public sealed class MarkRentalContractSignedRequest
{
    // storage_files.id của file PDF hợp đồng đã ký (upload qua signed-upload-ticket + confirm-upload).
    [Required(ErrorMessage = "Vui lòng tải lên bản hợp đồng đã ký.")]
    public Guid? ContractFileId { get; init; }
}
