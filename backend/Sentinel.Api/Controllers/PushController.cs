using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

/// <summary>Browser Web Push subscription management.</summary>
[Route("api/push")]
[Authorize(Policy = AuthPolicies.Operator)]
public class PushController(IPushSubscriptionService push) : ApiControllerBase
{
    /// <summary>VAPID public key the browser needs to subscribe (null when push is not configured).</summary>
    [HttpGet("vapid-public-key")]
    public VapidKeyResponse PublicKey() => new(push.PublicKey);

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(PushSubscriptionRequest request, CancellationToken ct)
    {
        await push.SubscribeAsync(request, CurrentUserId, Request.Headers.UserAgent.ToString(), ct);
        return StatusCode(StatusCodes.Status201Created, new { subscribed = true });
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(PushUnsubscribeRequest request, CancellationToken ct)
    {
        await push.UnsubscribeAsync(request.Endpoint, ct);
        return NoContent();
    }
}
