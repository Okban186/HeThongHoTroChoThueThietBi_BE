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
    Guid? CreatedById,
    string? CreatedByName,
    // Người đang đăng nhập được sửa công ty này (người tạo, hoặc công ty chưa có người tạo).
    bool CanEdit,
    int AccountCount,
    DateTimeOffset CreatedAt
);
