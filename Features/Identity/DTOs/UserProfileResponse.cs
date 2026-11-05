public record UserProfileResponse(
    Guid Id,
    string Username,
    string FullName,
    string? Email,
    string? Phone,
    string? Status,
    List<string> Roles,
    Guid? AvatarStorageFileId,
    string? AvatarUrl,
    StaffDto? Staff
);

public record StaffDto(
    Guid Id,
    Guid UserId,
    string EmployeeCode,
    string? Position,
    DateOnly? HireDate,
    string? Status
);