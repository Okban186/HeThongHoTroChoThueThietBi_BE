namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

/// <summary>
/// Thông tin tài khoản dùng cho màn hình quản trị.
/// </summary>
public sealed record UserAccountResponse(
    Guid Id,
    string Username,
    string FullName,
    string? Email,
    string? Phone,
    // ACTIVE, INACTIVE, LOCKED
    string Status,
    RoleResponse? Role,
    // Tài khoản của doanh nghiệp thuê (role COMPANY), do Sales quản lý.
    bool IsCompanyAccount,
    string? CompanyName,
    string? EmployeeCode,
    string? Position,
    DateOnly? HireDate,
    string? AvatarUrl,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset CreatedAt
);
