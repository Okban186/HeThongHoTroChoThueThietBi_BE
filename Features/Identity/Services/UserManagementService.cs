using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Identity.DTOs;
using HeThongHoTroChoThueThietBi.Features.Identity.Entities;
using HeThongHoTroChoThueThietBi.Features.Identity.Enums;
using HeThongHoTroChoThueThietBi.Features.Organization.Entities;
using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Services;

/// <summary>
/// Quản lý tài khoản dành cho Admin.
///
/// Quy tắc:
/// - Admin chỉ tạo tài khoản nhân viên (ADMIN, MANAGER, SALES, STOREKEEPER, TECHNICIAN).
///   Tài khoản COMPANY do Sales tạo cùng công ty.
/// - Mỗi tài khoản đúng 1 role. Mã nhân viên tự sinh: EMP-{ADM|MNG|SAL|STR|TEC}-NNN.
/// - Admin không tự đổi role/trạng thái/mật khẩu của chính mình qua API này.
/// - Hệ thống luôn còn ít nhất 1 Admin đang hoạt động.
/// - Khóa/ngừng hoạt động hoặc đặt lại mật khẩu sẽ thu hồi mọi refresh token của tài khoản.
///   Access token hiện tại cũng mất hiệu lực ngay vì mỗi request đều kiểm tra trạng thái trong DB.
/// </summary>
public sealed class UserManagementService(AppDbContext db, IFileUploadService uploadService)
{
    public const string AdminRoleCode = "ADMIN";

    /// <summary>Role nhân viên Admin được gán, kèm tiền tố mã nhân viên.</summary>
    private static readonly IReadOnlyDictionary<string, string> StaffRolePrefixes =
        new Dictionary<string, string>
        {
            ["ADMIN"] = "ADM",
            ["MANAGER"] = "MNG",
            ["SALES"] = "SAL",
            ["STOREKEEPER"] = "STR",
            ["LEAD_STOREKEEPER"] = "LEAD_STR",
            ["TECHNICIAN"] = "TEC",
        };

    public async Task<List<RoleResponse>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return roles.Select(ToRoleResponse).ToList();
    }

    public async Task<List<UserAccountResponse>> GetUsersAsync(
        string? keyword,
        string? roleCode,
        string? status,
        CancellationToken cancellationToken = default)
    {
        var query = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";

            query = query.Where(u =>
                EF.Functions.ILike(u.Username, pattern) ||
                EF.Functions.ILike(u.FullName, pattern) ||
                (u.Email != null && EF.Functions.ILike(u.Email, pattern)) ||
                (u.Phone != null && EF.Functions.ILike(u.Phone, pattern)) ||
                (u.Staff != null && EF.Functions.ILike(u.Staff.EmployeeCode, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(roleCode))
        {
            var code = roleCode.Trim().ToUpperInvariant();
            query = query.Where(u => u.Roles.Any(r => r.Code == code));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var userStatus = ParseStatus(status);
            query = query.Where(u => u.Status == userStatus);
        }

        var users = await query
            .Include(u => u.Roles)
            .Include(u => u.Staff)
            .Include(u => u.AvatarStorageFile)
            .OrderBy(u => u.FullName)
            .ToListAsync(cancellationToken);

        var companyNames = await GetCompanyNamesAsync(users.Select(u => u.Id).ToList(), cancellationToken);

        return users
            .Select(u => ToResponse(u, companyNames.GetValueOrDefault(u.Id)))
            .ToList();
    }

    public async Task<UserAccountResponse> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await LoadUserAsync(id, tracking: false, cancellationToken);

        return await ToResponseAsync(user, cancellationToken);
    }

    public async Task<UserAccountResponse> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var username = request.Username.Trim();
        var role = await FindStaffRoleAsync(request.RoleCode, cancellationToken);

        var usernameTaken = await db.Users
            .AnyAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);

        if (usernameTaken)
        {
            throw new AppException(
                "USERNAME_TAKEN",
                "Tên đăng nhập đã được sử dụng.",
                StatusCodes.Status409Conflict,
                "username");
        }

        var now = DateTimeOffset.UtcNow;
        var userId = Guid.CreateVersion7();

        var user = new User
        {
            Id = userId,
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName = request.FullName.Trim(),
            Email = NormalizeOptional(request.Email),
            Phone = NormalizeOptional(request.Phone),
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Staff = new Staff
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                EmployeeCode = await GenerateEmployeeCodeAsync(role.Code, cancellationToken),
                Position = NormalizeOptional(request.Position),
                HireDate = request.HireDate,
                Status = StaffStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            },
        };

        user.Roles.Add(role);
        db.Users.Add(user);

        await SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(userId, cancellationToken);
    }

    public async Task<UserAccountResponse> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await LoadUserAsync(id, tracking: true, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        user.FullName = request.FullName.Trim();
        user.Email = NormalizeOptional(request.Email);
        user.Phone = NormalizeOptional(request.Phone);
        user.UpdatedAt = now;

        if (user.Staff is not null)
        {
            user.Staff.Position = NormalizeOptional(request.Position);
            user.Staff.HireDate = request.HireDate;
            user.Staff.UpdatedAt = now;
        }

        await SaveChangesAsync(cancellationToken);

        return await ToResponseAsync(user, cancellationToken);
    }

    public async Task<UserAccountResponse> UpdateRoleAsync(
        Guid id,
        UpdateUserRoleRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        EnsureNotSelf(id, currentUserId, "Không thể tự đổi vai trò của chính mình.");

        var user = await LoadUserAsync(id, tracking: true, cancellationToken);

        if (user.Staff is null)
        {
            throw new AppException(
                "NOT_STAFF_ACCOUNT",
                "Chỉ đổi được vai trò của tài khoản nhân viên.",
                StatusCodes.Status400BadRequest);
        }

        var newRole = await FindStaffRoleAsync(request.RoleCode, cancellationToken);

        if (user.Roles.Count == 1 && user.Roles.First().Id == newRole.Id)
            return await ToResponseAsync(user, cancellationToken);

        if (IsActiveAdmin(user) && newRole.Code != AdminRoleCode)
            await EnsureAnotherActiveAdminAsync(user.Id, cancellationToken);

        user.Roles.Clear();
        user.Roles.Add(newRole);
        user.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);

        return await ToResponseAsync(user, cancellationToken);
    }

    public async Task<UserAccountResponse> UpdateStatusAsync(
        Guid id,
        UpdateUserStatusRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        EnsureNotSelf(id, currentUserId, "Không thể tự đổi trạng thái tài khoản của chính mình.");

        var newStatus = ParseStatus(request.Status);
        var user = await LoadUserAsync(id, tracking: true, cancellationToken);

        if (user.Status == newStatus)
            return await ToResponseAsync(user, cancellationToken);

        if (IsActiveAdmin(user) && newStatus != UserStatus.Active)
            await EnsureAnotherActiveAdminAsync(user.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        user.Status = newStatus;
        user.UpdatedAt = now;

        if (user.Staff is not null)
        {
            user.Staff.Status = newStatus == UserStatus.Active ? StaffStatus.Active : StaffStatus.Inactive;
            user.Staff.UpdatedAt = now;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await SaveChangesAsync(cancellationToken);

        if (newStatus != UserStatus.Active)
            await RevokeRefreshTokensAsync(user.Id, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await ToResponseAsync(user, cancellationToken);
    }

    public async Task ResetPasswordAsync(
        Guid id,
        ResetUserPasswordRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        EnsureNotSelf(id, currentUserId, "Hãy đổi mật khẩu của chính mình trong trang tài khoản.");

        var user = await LoadUserAsync(id, tracking: true, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.UpdatedAt = now;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await SaveChangesAsync(cancellationToken);
        await RevokeRefreshTokensAsync(user.Id, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<User> LoadUserAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Users : db.Users.AsNoTracking();

        return await query
                   .Include(u => u.Roles)
                   .Include(u => u.Staff)
                   .Include(u => u.AvatarStorageFile)
                   .SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
               ?? throw new AppException(
                   "USER_NOT_FOUND",
                   "Không tìm thấy tài khoản.",
                   StatusCodes.Status404NotFound);
    }

    private async Task<Role> FindStaffRoleAsync(string roleCode, CancellationToken cancellationToken)
    {
        var code = roleCode.Trim().ToUpperInvariant();

        if (!StaffRolePrefixes.ContainsKey(code))
        {
            throw new AppException(
                "ROLE_NOT_ASSIGNABLE",
                "Vai trò không hợp lệ. Tài khoản khách hàng (COMPANY) do bộ phận kinh doanh tạo.",
                StatusCodes.Status400BadRequest,
                "roleCode");
        }

        return await db.Roles.SingleOrDefaultAsync(r => r.Code == code, cancellationToken)
               ?? throw new AppException(
                   "ROLE_NOT_FOUND",
                   "Không tìm thấy vai trò.",
                   StatusCodes.Status400BadRequest,
                   "roleCode");
    }

    /// <summary>EMP-{tiền tố}-NNN, số tiếp theo sau số lớn nhất đang có.</summary>
    private async Task<string> GenerateEmployeeCodeAsync(string roleCode, CancellationToken cancellationToken)
    {
        var prefix = $"EMP-{StaffRolePrefixes[roleCode]}-";

        var existingCodes = await db.Staff
            .AsNoTracking()
            .Where(s => s.EmployeeCode.StartsWith(prefix))
            .Select(s => s.EmployeeCode)
            .ToListAsync(cancellationToken);

        var maxNumber = existingCodes
            .Select(code => int.TryParse(code[prefix.Length..], out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{maxNumber + 1:D3}";
    }

    private static bool IsActiveAdmin(User user)
        => user.Status == UserStatus.Active && user.Roles.Any(r => r.Code == AdminRoleCode);

    private async Task EnsureAnotherActiveAdminAsync(Guid excludeUserId, CancellationToken cancellationToken)
    {
        var hasAnotherAdmin = await db.Users.AnyAsync(u =>
                u.Id != excludeUserId &&
                u.Status == UserStatus.Active &&
                u.Roles.Any(r => r.Code == AdminRoleCode),
            cancellationToken);

        if (!hasAnotherAdmin)
        {
            throw new AppException(
                "LAST_ADMIN",
                "Hệ thống phải còn ít nhất một quản trị viên đang hoạt động.",
                StatusCodes.Status409Conflict);
        }
    }

    private static void EnsureNotSelf(Guid targetUserId, Guid currentUserId, string message)
    {
        if (targetUserId == currentUserId)
            throw new AppException("SELF_MODIFICATION_NOT_ALLOWED", message, StatusCodes.Status400BadRequest);
    }

    private Task RevokeRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    private static UserStatus ParseStatus(string status)
    {
        var value = status.Trim().ToUpperInvariant();

        return value switch
        {
            "ACTIVE" => UserStatus.Active,
            "INACTIVE" => UserStatus.Inactive,
            "LOCKED" => UserStatus.Locked,
            _ => throw new AppException(
                "INVALID_USER_STATUS",
                "Trạng thái không hợp lệ (ACTIVE, INACTIVE, LOCKED).",
                StatusCodes.Status400BadRequest,
                "status"),
        };
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Trùng username/mã nhân viên do hai thao tác đồng thời.
            throw new AppException(
                "DUPLICATE_ACCOUNT_DATA",
                "Tên đăng nhập hoặc mã nhân viên đã tồn tại, vui lòng thử lại.",
                StatusCodes.Status409Conflict);
        }
    }

    private async Task<Dictionary<Guid, string>> GetCompanyNamesAsync(
        List<Guid> userIds,
        CancellationToken cancellationToken)
    {
        return await (
            from companyUser in db.CompanyUsers.AsNoTracking()
            join company in db.Companies.AsNoTracking() on companyUser.CompanyId equals company.Id
            where userIds.Contains(companyUser.UserId)
            select new { companyUser.UserId, company.Name }
        ).ToDictionaryAsync(x => x.UserId, x => x.Name, cancellationToken);
    }

    private async Task<UserAccountResponse> ToResponseAsync(User user, CancellationToken cancellationToken)
    {
        var companyNames = await GetCompanyNamesAsync([user.Id], cancellationToken);

        return ToResponse(user, companyNames.GetValueOrDefault(user.Id));
    }

    private UserAccountResponse ToResponse(User user, string? companyName)
    {
        var role = user.Roles.OrderBy(r => r.Code).FirstOrDefault();
        var avatar = user.AvatarStorageFile;

        return new UserAccountResponse(
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            user.Phone,
            EnumName.ToDatabaseValue(user.Status),
            role is null ? null : ToRoleResponse(role),
            IsCompanyAccount: companyName is not null || user.Roles.Any(r => r.Code == "COMPANY"),
            companyName,
            user.Staff?.EmployeeCode,
            user.Staff?.Position,
            user.Staff?.HireDate,
            avatar is null
                ? null
                : uploadService.GetPrivateImageUrl(
                    $"{avatar.BucketName}/{avatar.ObjectKey}",
                    avatar.ResourceType.ToCloudinaryNetResourceType(),
                    avatar.ContentType!),
            user.LastLoginAt,
            user.CreatedAt);
    }

    private static RoleResponse ToRoleResponse(Role role)
        => new(role.Id, role.Code, role.Name, role.Description, StaffRolePrefixes.ContainsKey(role.Code));

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
