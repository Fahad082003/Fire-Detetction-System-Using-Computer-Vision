using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Sentinel.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Who performed the action (operator e-mail, or "device"), used in activity logs.</summary>
    protected string Actor => User.Identity?.Name ?? "unknown";

    protected int? CurrentUserId =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
