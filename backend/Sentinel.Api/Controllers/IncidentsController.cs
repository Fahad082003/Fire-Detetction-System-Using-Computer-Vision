using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

[Route("api/incidents")]
[Authorize(Policy = AuthPolicies.Operator)]
public class IncidentsController(IIncidentService incidents) : ApiControllerBase
{
    /// <summary>?resolved=true|false &amp;minSeverity=High &amp;limit=50</summary>
    [HttpGet]
    public async Task<IReadOnlyList<IncidentAlertDto>> List(
        [FromQuery] bool? resolved, [FromQuery] Severity? minSeverity, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        await incidents.ListAsync(resolved, minSeverity, limit, ct);

    [HttpGet("{id:int}")]
    public async Task<IncidentAlertDto> Get(int id, CancellationToken ct) => await incidents.GetAsync(id, ct);

    [HttpPost("{id:int}/resolve")]
    public async Task<IncidentAlertDto> Resolve(int id, CancellationToken ct) => await incidents.ResolveAsync(id, Actor, ct);

    /// <summary>Manual emergency dispatch: opens an incident and notifies responders immediately (cooldown bypassed).</summary>
    [HttpPost("dispatch")]
    [ProducesResponseType<IncidentAlertDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Dispatch(ManualDispatchRequest request, CancellationToken ct)
    {
        var incident = await incidents.DispatchAsync(request, Actor, ct);
        return CreatedAtAction(nameof(Get), new { id = incident.Id }, incident);
    }
}
