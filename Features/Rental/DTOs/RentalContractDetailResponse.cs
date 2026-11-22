namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

public sealed record RentalContractDetailResponse(
    RentalContractResponse Contract,
    string? DeliveryAddress,
    string? DeliveryNote,
    string? Note,
    List<RentalContractItemResponse> Items,
    List<RentalContractServiceResponse> Services,
    ContractRepresentativeResponse? Representative,
    List<ContractDocumentResponse> Documents,
    List<ContractStatusHistoryResponse> StatusHistory,
    string? ApprovedByName,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SignedAt,
    // URL có thời hạn của bản hợp đồng đã ký (nếu có).
    string? SignedContractUrl
);

public sealed record RentalContractItemResponse(
    Guid Id,
    Guid EquipmentModelId,
    string EquipmentModelCode,
    string EquipmentModelName,
    string Unit,
    int Quantity,
    // DAILY, MONTHLY
    string BillingUnit,
    decimal AgreedUnitPrice,
    // Số ngày (DAILY) hoặc số tháng (MONTHLY) tính tiền.
    int BillableUnits,
    decimal Amount,
    string? Note
);

public sealed record RentalContractServiceResponse(
    Guid Id,
    // DELIVERY, LOADING, UNLOADING, OTHER
    string ServiceType,
    // RENTAL_COMPANY, CUSTOMER
    string ProvidedBy,
    int Quantity,
    decimal UnitPrice,
    decimal Amount,
    string? Note
);

public sealed record ContractRepresentativeResponse(
    string FullName,
    string IdNumber,
    string? Phone,
    string? Email,
    // LEGAL_REPRESENTATIVE, AUTHORIZED_REPRESENTATIVE
    string RepresentativeType
);

public sealed record ContractDocumentResponse(
    Guid Id,
    // ID_CARD_FRONT, ID_CARD_BACK, BUSINESS_LICENSE, AUTHORIZATION_LETTER, OTHER
    string Kind,
    Guid StorageFileId,
    string? OriginalName,
    // URL có thời hạn để xem ảnh.
    string? Url
);

public sealed record ContractStatusHistoryResponse(
    string Status,
    string PreviousStatus,
    string? ChangedByName,
    DateTimeOffset ChangedAt,
    string? Note
);
