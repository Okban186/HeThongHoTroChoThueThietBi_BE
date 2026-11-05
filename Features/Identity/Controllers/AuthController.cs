using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Features.Identity.DTOs;
using HeThongHoTroChoThueThietBi.Features.Identity.Entities;
using HeThongHoTroChoThueThietBi.Features.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HeThongHoTroChoThueThietBi.Features.Identity.Enums;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;

namespace HeThongHoTroChoThueThietBi.Features.Identity.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AppDbContext db, IFileUploadService uploadService, TokenService tokens) : ControllerBase
{
    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == request.Username, ct);
        if (user is null || user.Status != Enums.UserStatus.Active || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { message = "Tên đăng nhập hoặc mật khẩu không đúng." });

        var roles = await (from ur in db.UserRoles
                           join r in db.Roles on ur.RoleId equals r.Id
                           where ur.UserId == user.Id
                           select r.Code).ToListAsync(ct);
        var access = tokens.CreateAccessToken(user, roles);
        var refresh = tokens.CreateRefreshToken();
        var familyId = Guid.CreateVersion7();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = refresh.Hash,
            FamilyId = familyId,
            ExpiresAt = refresh.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow
        });
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Response.Headers["X-New-Access-Token"] = access.Token;
        Response.Headers["X-Refresh-Token"] = refresh.RawToken;
        Response.Headers["Cache-Control"] = "no-store";

        return Ok();
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var userId = Guid.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)!
        );

        var jtiValue = User.FindFirstValue(JwtRegisteredClaimNames.Jti);

        if (Guid.TryParse(jtiValue, out var jti))
        {
            var exp = long.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Exp)!);

            var expires = DateTimeOffset.FromUnixTimeSeconds(exp);

            if (expires > DateTimeOffset.UtcNow &&
                !await db.RevokedTokens.AnyAsync(x => x.Jti == jti, ct))
            {
                db.RevokedTokens.Add(new RevokedToken
                {
                    Jti = jti,
                    UserId = userId,
                    ExpiresAt = expires,
                    RevokedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var refreshToken = Request.Headers["X-Refresh-Token"].ToString();

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = TokenService.HashRefreshToken(refreshToken);

            var refresh = await db.RefreshTokens
                .SingleOrDefaultAsync(
                    x => x.TokenHash == hash && x.UserId == userId,
                    ct
                );

            if (refresh is not null && refresh.RevokedAt is null)
                refresh.RevokedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMyProfile(CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub"));

        var user = await db.Users
         .Include(u => u.Staff)
         .Include(u => u.AvatarStorageFile)
         .Include(u => u.Roles)
         .AsNoTracking()
         .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null) return NotFound();

        StorageFile? storageFile = user.AvatarStorageFile;

        // Mapping nhanh gọn bằng C# Expression
        var response = new UserProfileResponse(
            user.Id,
            user.Username,
            user.FullName,
            user.Email,
            user.Phone,
            user.Status.ToString(),
            user.Roles.Select(r => new RoleDto(
                r.Id,
                r.Code,
                 r.Name,
                  r.Description
            )).ToList(),
            user.AvatarStorageFileId,
            storageFile != null ? uploadService.GetPrivateImageUrl($"{storageFile.BucketName}/{storageFile.ObjectKey}", CloudinaryMappingExtensions.ToCloudinaryNetResourceType(storageFile.ResourceType), storageFile.ContentType!) : null,
            user.Staff == null ? null : new StaffDto(
                user.Staff.Id,
                user.Staff.UserId,
                user.Staff.EmployeeCode,
                user.Staff.Position,
                user.Staff.HireDate,
                user.Staff.Status.ToString()
            )
        );

        return Ok(response);
    }
}
