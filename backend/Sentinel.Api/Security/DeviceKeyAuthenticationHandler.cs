using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;

namespace Sentinel.Api.Security;

/// <summary>
/// Authenticates machines (AI pipelines, IoT gateways, the simulator) by a shared secret.
/// REST: header "X-Device-Key". SignalR: the same header, or "?device_key=" on /hubs URLs.
/// If no key is configured the scheme never succeeds, i.e. device access is disabled.
/// </summary>
public class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<DeviceOptions> device) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = device.Value.ApiKey;
        if (string.IsNullOrEmpty(configured)) return Task.FromResult(AuthenticateResult.NoResult());

        string? presented = Request.Headers[AuthPolicies.DeviceHeader];
        if (string.IsNullOrEmpty(presented) && Request.Path.StartsWithSegments("/hubs"))
            presented = Request.Query["device_key"];
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configured)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid device key."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "device"), new Claim(ClaimTypes.Role, AuthPolicies.DeviceRole)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
