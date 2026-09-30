using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

[Route("api/auth")]
public class AuthController(IAuthService auth) : ApiControllerBase
{
    /// <summary>Operator login. Returns a JWT to send as "Authorization: Bearer &lt;token&gt;".</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<LoginResponse> Login(LoginRequest request, CancellationToken ct) =>
        await auth.LoginAsync(request, ct);

    /// <summary>The currently logged-in operator.</summary>
    [HttpGet("me")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<UserDto> Me(CancellationToken ct) =>
        await auth.GetUserAsync(CurrentUserId ?? 0, ct);
}
