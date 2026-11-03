
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using HeThongHoTroChoThueThietBi.Features.Identity.Entities;
using HeThongHoTroChoThueThietBi.Features.Identity.Enums;
using HeThongHoTroChoThueThietBi.Features.Identity.Services;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HeThongHoTroChoThueThietBi.Common.Security;

public sealed class JwtAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db,
    TokenService tokenService,
    IOptions<JwtOptions> jwtOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(
        options, logger, encoder)
{
    public const string SchemeName = "CustomJwt";

    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Lấy Access Token từ Authorization header.
        var authorization = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorization))
            return AuthenticateResult.NoResult();

        if (!authorization.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("Authorization header không hợp lệ.");
        }

        var accessToken = authorization["Bearer ".Length..].Trim();

        if (string.IsNullOrWhiteSpace(accessToken))
            return AuthenticateResult.Fail("Access Token không hợp lệ.");

        // 2. Xác thực chữ ký, Issuer và Audience.
        // Tắt ValidateLifetime để tự xử lý Access Token hết hạn.
        var tokenHandler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        ClaimsPrincipal principal;
        JwtSecurityToken jwt;

        try
        {
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _jwtOptions.Issuer,

                ValidateAudience = true,
                ValidAudience = _jwtOptions.Audience,

                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(_jwtOptions.SecretKey)
                ),

                ValidateLifetime = false,
                RequireExpirationTime = true,
                RequireSignedTokens = true,

                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

                ClockSkew = TimeSpan.Zero
            };

            principal = tokenHandler.ValidateToken(
                accessToken,
                validationParameters,
                out var validatedToken
            );

            jwt = validatedToken as JwtSecurityToken
                ?? throw new SecurityTokenException("JWT không hợp lệ.");
        }
        catch (Exception ex) when (
            ex is SecurityTokenException ||
            ex is ArgumentException ||
            ex is FormatException)
        {
            return AuthenticateResult.Fail("Access Token không hợp lệ.");
        }

        var now = DateTimeOffset.UtcNow;

        // 3. Kiểm tra thời điểm bắt đầu hiệu lực.
        if (jwt.ValidFrom > now.UtcDateTime)
            return AuthenticateResult.Fail("Access Token chưa có hiệu lực.");

        // 4. Lấy User ID và JTI từ JWT.
        // Chỉ sử dụng để xác định tài khoản và token.
        var userIdValue = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var jtiValue = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        if (!Guid.TryParse(userIdValue, out var userId) ||
            !Guid.TryParse(jtiValue, out var jti))
        {
            return AuthenticateResult.Fail(
                "Access Token thiếu thông tin định danh."
            );
        }

        // 5. Kiểm tra Access Token có bị thu hồi không.
        var isRevoked = await db.RevokedTokens
            .AnyAsync(
                x => x.Jti == jti,
                Context.RequestAborted
            );

        if (isRevoked)
            return AuthenticateResult.Fail("Access Token đã bị thu hồi.");

        // 6. Lấy thông tin User mới nhất từ database.
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == userId,
                Context.RequestAborted
            );

        if (user is null || user.Status != UserStatus.Active)
            return AuthenticateResult.Fail("Tài khoản không hoạt động.");

        // 7. Lấy Role hiện tại từ database.
        var roles = await (
            from userRole in db.UserRoles
            join role in db.Roles
                on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Code
        ).ToListAsync(Context.RequestAborted);

        // 8. Kiểm tra thời hạn Access Token.
        var accessTokenExpiresAt = new DateTimeOffset(jwt.ValidTo);

        // Trường hợp Access Token vẫn còn hiệu lực.
        if (now < accessTokenExpiresAt)
        {
            // Xây dựng Claims từ dữ liệu mới nhất trong database.
            var claims = CreateUserClaims(user, roles, jti, jwt.ValidTo);

            var identity = new ClaimsIdentity(
                claims,
                Scheme.Name,
                ClaimTypes.Name,
                ClaimTypes.Role
            );

            var currentPrincipal = new ClaimsPrincipal(identity);

            return AuthenticateResult.Success(
                new AuthenticationTicket(
                    currentPrincipal,
                    Scheme.Name
                )
            );
        }

        // 9. Access Token đã hết hạn: kiểm tra Refresh Token.
        var refreshToken = Request.Headers["X-Refresh-Token"]
            .ToString()
            .Trim();

        if (string.IsNullOrWhiteSpace(refreshToken))
            return AuthenticateResult.Fail("Thiếu Refresh Token.");

        var refreshTokenHash = TokenService.HashRefreshToken(refreshToken);

        var storedRefreshToken = await db.RefreshTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TokenHash == refreshTokenHash &&
                     x.UserId == userId,
                Context.RequestAborted
            );

        if (storedRefreshToken is null ||
            storedRefreshToken.RevokedAt is not null ||
            storedRefreshToken.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail(
                "Refresh Token không hợp lệ hoặc đã hết hạn."
            );
        }

        // 10. Tạo Access Token và Refresh Token mới.
        var newAccessToken = tokenService.CreateAccessToken(user, roles);
        var newRefreshToken = tokenService.CreateRefreshToken();

        // 11. Thu hồi Refresh Token cũ và lưu Refresh Token mới.
        var nextRefreshToken = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = newRefreshToken.Hash,
            FamilyId = storedRefreshToken.FamilyId,
            ExpiresAt = newRefreshToken.ExpiresAt,
            CreatedAt = DateTimeOffset.UtcNow
        };

        storedRefreshToken.RevokedAt = DateTimeOffset.UtcNow;
        storedRefreshToken.ReplacedByTokenId = nextRefreshToken.Id;

        db.RefreshTokens.Add(nextRefreshToken);

        await db.SaveChangesAsync(Context.RequestAborted);

        // 12. Trả hai token mới qua Response Headers.
        Response.Headers["X-New-Access-Token"] = newAccessToken.Token;
        Response.Headers["X-Refresh-Token"] = newRefreshToken.RawToken;
        Response.Headers["Cache-Control"] = "no-store";

        // 12. Tạo ClaimsPrincipal từ thông tin mới nhất trong database.
        var newClaims = CreateUserClaims(
     user,
     roles,
     newAccessToken.Jti,
     newAccessToken.ExpiresAt.UtcDateTime
 );

        var newIdentity = new ClaimsIdentity(
            newClaims,
            Scheme.Name,
            ClaimTypes.Name,
            ClaimTypes.Role
        );

        var newPrincipal = new ClaimsPrincipal(newIdentity);

        // Request hiện tại tiếp tục đến Controller.
        return AuthenticateResult.Success(
            new AuthenticationTicket(
                newPrincipal,
                Scheme.Name
            )
        );
    }

    private static List<Claim> CreateUserClaims(
     Features.Identity.Entities.User user,
     IEnumerable<string> roles,
     Guid jti,
     DateTime expiresAt)
    {
        var claims = new List<Claim>
    {
        new(
            JwtRegisteredClaimNames.Sub,
            user.Id.ToString()
        ),
        new(
            JwtRegisteredClaimNames.Jti,
            jti.ToString()
        ),
        new(
            JwtRegisteredClaimNames.Exp,
            new DateTimeOffset(expiresAt)
                .ToUnixTimeSeconds()
                .ToString(),
            ClaimValueTypes.Integer64
        ),
        new(
            ClaimTypes.NameIdentifier,
            user.Id.ToString()
        ),
        new(
            ClaimTypes.Name,
            user.Username
        )
    };

        claims.AddRange(
            roles.Select(role => new Claim(ClaimTypes.Role, role))
        );

        return claims;
    }
}
