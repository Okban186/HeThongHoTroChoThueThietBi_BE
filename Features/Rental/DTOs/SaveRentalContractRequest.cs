using System.ComponentModel.DataAnnotations;

namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>
/// Tạo hợp đồng từ phiếu giữ chỗ đã xác nhận.
/// Thời gian thuê, model và số lượng lấy theo phiếu; Sales chọn đơn vị tính và đơn giá.
/// </summary>
public sealed class CreateRentalContractRequest : RentalContractContent
{
    [Required(ErrorMessage = "Vui lòng chọn phiếu giữ chỗ.")]
    public Guid? ReservationId { get; init; }
}

/// <summary>Sửa hợp đồng khi còn ở trạng thái Bản nháp hoặc Bị từ chối.</summary>
public sealed class UpdateRentalContractRequest : RentalContractContent;

public abstract class RentalContractContent
{
    // SHORT_TERM hoặc LONG_TERM.
    [Required(ErrorMessage = "Vui lòng chọn loại thuê.")]
    public string RentalType { get; init; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Địa chỉ giao hàng tối đa 500 ký tự.")]
    public string? DeliveryAddress { get; init; }

    [MaxLength(1000, ErrorMessage = "Ghi chú giao hàng tối đa 1000 ký tự.")]
    public string? DeliveryNote { get; init; }

    [MaxLength(2000, ErrorMessage = "Ghi chú tối đa 2000 ký tự.")]
    public string? Note { get; init; }

    [Range(0, 999_999_999_999, ErrorMessage = "Tiền cọc không hợp lệ.")]
    public decimal DepositRequiredAmount { get; init; }

    // Mỗi model của phiếu đúng một dòng.
    [Required, MinLength(1, ErrorMessage = "Hợp đồng phải có thiết bị.")]
    public List<RentalContractItemRequest> Items { get; init; } = [];

    [MaxLength(20)]
    public List<RentalContractServiceRequest> Services { get; init; } = [];

    [Required(ErrorMessage = "Vui lòng nhập người đại diện ký hợp đồng.")]
    public ContractRepresentativeRequest? Representative { get; init; }

    [Required(ErrorMessage = "Vui lòng tải lên giấy tờ.")]
    public ContractDocumentsRequest? Documents { get; init; }
}

public sealed class RentalContractItemRequest
{
    [Required]
    public Guid? EquipmentModelId { get; init; }

    // DAILY hoặc MONTHLY.
    [Required(ErrorMessage = "Vui lòng chọn đơn vị tính.")]
    public string BillingUnit { get; init; } = string.Empty;

    [Range(0, 999_999_999_999, ErrorMessage = "Đơn giá không hợp lệ.")]
    public decimal AgreedUnitPrice { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class RentalContractServiceRequest
{
    // DELIVERY, LOADING, UNLOADING, OTHER.
    [Required(ErrorMessage = "Vui lòng chọn loại dịch vụ.")]
    public string ServiceType { get; init; } = string.Empty;

    // RENTAL_COMPANY (bên cho thuê làm, tính tiền) hoặc CUSTOMER (khách tự làm).
    [Required(ErrorMessage = "Vui lòng chọn bên thực hiện.")]
    public string ProvidedBy { get; init; } = string.Empty;

    [Range(1, 9999, ErrorMessage = "Số lượng từ 1 đến 9999.")]
    public int Quantity { get; init; } = 1;

    [Range(0, 999_999_999_999, ErrorMessage = "Đơn giá không hợp lệ.")]
    public decimal UnitPrice { get; init; }

    [MaxLength(500)]
    public string? Note { get; init; }
}

public sealed class ContractRepresentativeRequest
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên người đại diện.")]
    [MaxLength(150)]
    public string FullName { get; init; } = string.Empty;

    // Số CCCD 12 số (hoặc CMND cũ 9 số).
    [Required(ErrorMessage = "Vui lòng nhập số CCCD.")]
    [RegularExpression(@"^(\d{9}|\d{12})$", ErrorMessage = "Số CCCD gồm 12 chữ số (CMND cũ: 9 số).")]
    public string IdNumber { get; init; } = string.Empty;

    [RegularExpression("^[0-9+ ]{8,20}$", ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string? Phone { get; init; }

    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    [MaxLength(255)]
    public string? Email { get; init; }

    // LEGAL_REPRESENTATIVE hoặc AUTHORIZED_REPRESENTATIVE.
    [Required(ErrorMessage = "Vui lòng chọn tư cách người ký.")]
    public string RepresentativeType { get; init; } = string.Empty;
}

/// <summary>
/// Id của storage_files đã upload và xác nhận qua
/// POST /api/rental-contracts/upload-ticket và /api/rental-contracts/confirm-upload.
/// </summary>
public sealed class ContractDocumentsRequest
{
    [Required(ErrorMessage = "Vui lòng tải ảnh CCCD mặt trước.")]
    public Guid? IdCardFrontFileId { get; init; }

    [Required(ErrorMessage = "Vui lòng tải ảnh CCCD mặt sau.")]
    public Guid? IdCardBackFileId { get; init; }

    [Required(ErrorMessage = "Vui lòng tải ảnh giấy phép kinh doanh.")]
    public Guid? BusinessLicenseFileId { get; init; }

    // Bắt buộc khi người ký là người được ủy quyền.
    public Guid? AuthorizationLetterFileId { get; init; }
}
