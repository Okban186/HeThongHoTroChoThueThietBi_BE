namespace HeThongHoTroChoThueThietBi.Features.Organization.DTOs;

/// <summary>Công ty khách hàng (danh sách).</summary>
public sealed record CompanyResponse(
    Guid Id,
    string CompanyCode,
    string Name,
    string TaxCode,
    string? Address,
    // ACTIVE, INACTIVE
    string Status,
    // Người tạo: chỉ để biết trách nhiệm, không giới hạn quyền sửa.
    Guid? CreatedById,
    string? CreatedByName,
    int AccountCount,
    DateTimeOffset CreatedAt
);
