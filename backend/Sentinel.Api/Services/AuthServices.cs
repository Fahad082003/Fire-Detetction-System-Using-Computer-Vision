using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

public class TokenService(IOptions<JwtOptions> options) : ITokenService
{
    public (string Token, DateTime ExpiresAt) CreateToken(AppUser user)
    {
        var jwt = options.Value;
        var expires = DateTime.UtcNow.AddMinutes(jwt.ExpiryMinutes);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim("display_name", user.DisplayName),
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims, expires: expires, signingCredentials: credentials);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public class AuthService(SentinelDbContext db, ITokenService tokens, IPasswordHasher<AppUser> hasher) : IAuthService
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        // Same message for "no such user" and "wrong password" so attackers cannot probe accounts.
        if (user is null || !user.IsActive)
            throw new UnauthorizedAppException("Invalid email or password.");

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
            throw new UnauthorizedAppException("Invalid email or password.");
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password);

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var (token, expires) = tokens.CreateToken(user);
        return new LoginResponse(token, expires, user.ToDto());
    }

    public async Task<UserDto> GetUserAsync(int id, CancellationToken ct = default) =>
        (await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct)
         ?? throw new NotFoundException("User not found.")).ToDto();
}

public class PushSubscriptionService(SentinelDbContext db, IOptions<NotificationOptions> options) : IPushSubscriptionService
{
    public string? PublicKey => string.IsNullOrWhiteSpace(options.Value.WebPush.PublicKey) ? null : options.Value.WebPush.PublicKey;

    public async Task SubscribeAsync(PushSubscriptionRequest request, int? userId, string userAgent, CancellationToken ct = default)
    {
        var sub = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
        if (sub is null)
        {
            sub = new WebPushSubscription { Endpoint = request.Endpoint };
            db.PushSubscriptions.Add(sub);
        }
        sub.P256dh = request.Keys.P256dh;
        sub.Auth = request.Keys.Auth;
        sub.UserId = userId;
        sub.UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent;
        sub.IsActive = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(string endpoint, CancellationToken ct = default) =>
        await db.PushSubscriptions.Where(s => s.Endpoint == endpoint).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
}
