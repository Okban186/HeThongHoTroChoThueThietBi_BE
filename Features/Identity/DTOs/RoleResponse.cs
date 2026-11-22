namespace HeThongHoTroChoThueThietBi.Features.Identity.DTOs;

public sealed record RoleResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    // true: Admin được gán role này cho tài khoản nhân viên.
    bool AssignableByAdmin
);
