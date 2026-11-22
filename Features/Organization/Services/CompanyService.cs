using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Identity.Entities;
using HeThongHoTroChoThueThietBi.Features.Identity.Enums;
using HeThongHoTroChoThueThietBi.Features.Organization.DTOs;
using HeThongHoTroChoThueThietBi.Features.Organization.Entities;
using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Organization.Services;

/// <summary>
/// Công ty khách hàng do nhân viên kinh doanh quản lý.
///
/// Quy tắc:
/// - Sales xem được mọi công ty; chỉ người tạo (companies.created_by) được sửa,
///   đổi trạng thái và tạo tài khoản. Công ty chưa có người tạo (dữ liệu cũ) thì Sales nào cũng sửa được.
/// - Mã công ty tự sinh: COMP-NNNN. Mã số thuế không trùng.
/// - Tài khoản đăng nhập của khách hàng có role COMPANY, liên kết qua company_users.
///   Một công ty có thể có nhiều tài khoản; chỉ tạo được khi công ty đang hoạt động.
/// </summary>
public sealed class CompanyService(AppDbContext db)
{
    private const string CompanyCodePrefix = "COMP-";
    private const string CompanyRoleCode = "COMPANY";

    public async Task<List<CompanyResponse>> GetCompaniesAsync(
        string? keyword,
        string? status,
        bool onlyMine,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var query = db.Companies.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";

            query = query.Where(c =>
                EF.Functions.ILike(c.CompanyCode, pattern) ||
                EF.Functions.ILike(c.Name, pattern) ||
                EF.Functions.ILike(c.TaxCode, pattern) ||
                (c.Address != null && EF.Functions.ILike(c.Address, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var companyStatus = ParseStatus(status);
            query = query.Where(c => c.Status == companyStatus);
        }

        if (onlyMine)
            query = query.Where(c => c.CreatedBy == currentUserId);

        var companies = await query
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return await ToResponsesAsync(companies, currentUserId, cancellationToken);
    }

    public async Task<CompanyDetailResponse> GetDetailAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var company = await FindCompanyAsync(id, tracking: false, cancellationToken);
        var response = (await ToResponsesAsync([company], currentUserId, cancellationToken)).Single();

        var accounts = await (
            from companyUser in db.CompanyUsers.AsNoTracking()
            join user in db.Users.AsNoTracking() on companyUser.UserId equals user.Id
            where companyUser.CompanyId == id
            orderby user.CreatedAt
            select user
        ).ToListAsync(cancellationToken);

        return new CompanyDetailResponse(
            response,
            accounts.Select(u => new CompanyAccountResponse(
                u.Id,
                u.Username,
                u.FullName,
                u.Email,
                u.Phone,
                EnumName.ToDatabaseValue(u.Status),
                u.LastLoginAt,
                u.CreatedAt)).ToList());
    }

    public async Task<CompanyDetailResponse> CreateAsync(
        CreateCompanyRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var taxCode = request.TaxCode.Trim();
        await EnsureTaxCodeAvailableAsync(taxCode, excludeCompanyId: null, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        var company = new Company
        {
            Id = Guid.CreateVersion7(),
            CompanyCode = await GenerateCompanyCodeAsync(cancellationToken),
            Name = request.Name.Trim(),
            TaxCode = taxCode,
            Address = NormalizeOptional(request.Address),
            Status = CompanyStatus.Active,
            CreatedBy = currentUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Companies.Add(company);
        await SaveChangesAsync(cancellationToken);

        return await GetDetailAsync(company.Id, currentUserId, cancellationToken);
    }

    public async Task<CompanyDetailResponse> UpdateAsync(
        Guid id,
        UpdateCompanyRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var company = await FindCompanyAsync(id, tracking: true, cancellationToken);
        EnsureCanEdit(company, currentUserId);

        var taxCode = request.TaxCode.Trim();

        if (taxCode != company.TaxCode)
            await EnsureTaxCodeAvailableAsync(taxCode, excludeCompanyId: id, cancellationToken);

        company.Name = request.Name.Trim();
        company.TaxCode = taxCode;
        company.Address = NormalizeOptional(request.Address);
        company.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    public async Task<CompanyDetailResponse> UpdateStatusAsync(
        Guid id,
        UpdateCompanyStatusRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var company = await FindCompanyAsync(id, tracking: true, cancellationToken);
        EnsureCanEdit(company, currentUserId);

        var status = ParseStatus(request.Status);

        if (company.Status != status)
        {
            company.Status = status;
            company.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveChangesAsync(cancellationToken);
        }

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    public async Task<CompanyDetailResponse> CreateAccountAsync(
        Guid companyId,
        CreateCompanyAccountRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var company = await FindCompanyAsync(companyId, tracking: false, cancellationToken);
        EnsureCanEdit(company, currentUserId);

        if (company.Status != CompanyStatus.Active)
        {
            throw new AppException(
                "COMPANY_INACTIVE",
                "Công ty đang ngừng hoạt động, không thể tạo tài khoản.",
                StatusCodes.Status409Conflict);
        }

        var username = request.Username.Trim();

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

        var companyRole = await db.Roles.SingleAsync(r => r.Code == CompanyRoleCode, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName = request.FullName.Trim(),
            Email = NormalizeOptional(request.Email),
            Phone = NormalizeOptional(request.Phone),
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        user.Roles.Add(companyRole);
        db.Users.Add(user);
        db.CompanyUsers.Add(new CompanyUser
        {
            UserId = user.Id,
            CompanyId = companyId,
            CreatedAt = now,
        });

        await SaveChangesAsync(cancellationToken);

        return await GetDetailAsync(companyId, currentUserId, cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Company> FindCompanyAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Companies : db.Companies.AsNoTracking();

        return await query.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
               ?? throw new AppException(
                   "COMPANY_NOT_FOUND",
                   "Không tìm thấy công ty.",
                   StatusCodes.Status404NotFound);
    }

    private static bool CanEdit(Company company, Guid currentUserId)
        => company.CreatedBy is null || company.CreatedBy == currentUserId;

    private static void EnsureCanEdit(Company company, Guid currentUserId)
    {
        if (!CanEdit(company, currentUserId))
        {
            throw new AppException(
                "COMPANY_NOT_OWNED",
                "Chỉ nhân viên tạo công ty này mới được chỉnh sửa.",
                StatusCodes.Status403Forbidden);
        }
    }

    private async Task EnsureTaxCodeAvailableAsync(
        string taxCode,
        Guid? excludeCompanyId,
        CancellationToken cancellationToken)
    {
        var taken = await db.Companies.AnyAsync(
            c => c.TaxCode == taxCode && (excludeCompanyId == null || c.Id != excludeCompanyId),
            cancellationToken);

        if (taken)
        {
            throw new AppException(
                "TAX_CODE_TAKEN",
                "Mã số thuế đã thuộc về một công ty khác.",
                StatusCodes.Status409Conflict,
                "taxCode");
        }
    }

    /// <summary>COMP-NNNN, số tiếp theo sau số lớn nhất đang có.</summary>
    private async Task<string> GenerateCompanyCodeAsync(CancellationToken cancellationToken)
    {
        var codes = await db.Companies
            .AsNoTracking()
            .Where(c => c.CompanyCode.StartsWith(CompanyCodePrefix))
            .Select(c => c.CompanyCode)
            .ToListAsync(cancellationToken);

        var maxNumber = codes
            .Select(code => int.TryParse(code[CompanyCodePrefix.Length..], out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{CompanyCodePrefix}{maxNumber + 1:D4}";
    }

    private async Task<List<CompanyResponse>> ToResponsesAsync(
        List<Company> companies,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        var companyIds = companies.Select(c => c.Id).ToList();
        var creatorIds = companies.Where(c => c.CreatedBy != null).Select(c => c.CreatedBy!.Value).Distinct().ToList();

        var accountCounts = await db.CompanyUsers
            .AsNoTracking()
            .Where(cu => companyIds.Contains(cu.CompanyId))
            .GroupBy(cu => cu.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, cancellationToken);

        var creatorNames = await db.Users
            .AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);

        return companies.Select(c => new CompanyResponse(
            c.Id,
            c.CompanyCode,
            c.Name,
            c.TaxCode,
            c.Address,
            EnumName.ToDatabaseValue(c.Status),
            c.CreatedBy,
            c.CreatedBy is null ? null : creatorNames.GetValueOrDefault(c.CreatedBy.Value),
            CanEdit(c, currentUserId),
            accountCounts.GetValueOrDefault(c.Id),
            c.CreatedAt)).ToList();
    }

    private static CompanyStatus ParseStatus(string status)
    {
        return status.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => CompanyStatus.Active,
            "INACTIVE" => CompanyStatus.Inactive,
            _ => throw new AppException(
                "INVALID_COMPANY_STATUS",
                "Trạng thái không hợp lệ (ACTIVE, INACTIVE).",
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
            // Trùng mã công ty / mã số thuế / username do hai thao tác đồng thời.
            throw new AppException(
                "DUPLICATE_COMPANY_DATA",
                "Dữ liệu bị trùng (mã công ty, mã số thuế hoặc tên đăng nhập), vui lòng thử lại.",
                StatusCodes.Status409Conflict);
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
