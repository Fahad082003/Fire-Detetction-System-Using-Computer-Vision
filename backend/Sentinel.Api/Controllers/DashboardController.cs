using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

[Route("api/dashboard")]
[Authorize(Policy = AuthPolicies.Operator)]
public class DashboardController(IDashboardService dashboard) : ApiControllerBase
{
    /// <summary>Complete dashboard state: cameras, sensors (with history), sprinklers, recent incidents, system level.</summary>
    [HttpGet("snapshot")]
    public async Task<DashboardSnapshotDto> Snapshot(CancellationToken ct) => await dashboard.GetSnapshotAsync(ct);
}
