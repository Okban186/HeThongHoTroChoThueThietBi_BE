namespace HeThongHoTroChoThueThietBi.Features.Organization.DTOs;

/// <summary>Tài khoản đăng nhập (role COMPANY) thuộc một công ty.</summary>
public sealed record CompanyAccountResponse(
    Guid Id,
    string Username,
    string FullName,
    string? Email,
    string? Phone,
    // ACTIVE, INACTIVE, LOCKED
    string Status,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt
);
