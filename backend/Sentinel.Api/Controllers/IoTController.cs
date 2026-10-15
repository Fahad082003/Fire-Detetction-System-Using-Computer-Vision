using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

/// <summary>
/// IoT endpoints: read sensors, push (or simulate) Smoke-PPM / Temperature readings,
/// and control Smart Sprinklers (on/off, Auto/Manual, master switch).
/// </summary>
[Route("api/iot")]
[Authorize(Policy = AuthPolicies.OperatorOrDevice)]
public class IoTController(ISensorService sensors, ISprinklerService sprinklers) : ApiControllerBase
{
    // ------------------------------------------------------------------ sensors
    [HttpGet("sensors")]
    public async Task<IReadOnlyList<SensorNodeDto>> GetSensors(CancellationToken ct) => await sensors.GetAllAsync(ct);

    [HttpGet("sensors/{id}")]
    public async Task<SensorNodeDto> GetSensor(string id, CancellationToken ct) => await sensors.GetAsync(id, ct);

    [HttpGet("sensors/{id}/history")]
    public async Task<SensorHistoryDto> GetHistory(string id, [FromQuery] int limit = 120, [FromQuery] DateTime? since = null, CancellationToken ct = default) =>
        await sensors.GetHistoryAsync(id, limit, since, ct);

    /// <summary>Set one sensor's current value (a real gateway reading or a manual simulation from Swagger).</summary>
    [HttpPost("sensors/{id}/readings")]
    public async Task<SensorNodeDto> PostReading(string id, SensorReadingRequest request, CancellationToken ct) =>
        await sensors.IngestReadingAsync(id, request.Value, request.RecordedAt, ct);

    /// <summary>Gateway batch: many sensors in one request. Unknown sensors are reported, not fatal.</summary>
    [HttpPost("telemetry")]
    public async Task<ActionResult<BulkTelemetryResult>> PostTelemetry(BulkTelemetryRequest request, CancellationToken ct)
    {
        var result = await sensors.IngestBulkAsync(request.Readings, request.Simulated, ct);
        return result.Errors.Count > 0 && result.Updated.Count == 0 ? BadRequest(result) : Ok(result);
    }

    // ------------------------------------------------------------------ sprinklers
    [HttpGet("sprinklers")]
    public async Task<IReadOnlyList<SmartSprinklerDto>> GetSprinklers(CancellationToken ct) => await sprinklers.GetAllAsync(ct);

    /// <summary>{ "isActive": true|false, "mode": "Auto"|"Manual" } — both optional; empty body flips the relay.</summary>
    [HttpPost("sprinklers/{id}/toggle")]
    public async Task<SmartSprinklerDto> Toggle(string id, [FromBody] ToggleSprinklerRequest? request, CancellationToken ct) =>
        await sprinklers.ToggleAsync(id, request ?? new ToggleSprinklerRequest(), Actor, ct);

    /// <summary>System-wide (or per-zone) sprinkler switch. Operators only.</summary>
    [HttpPost("sprinklers/master")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<MasterSprinklerResult> Master(MasterSprinklerRequest request, CancellationToken ct) =>
        await sprinklers.SetAllAsync(request.IsActive!.Value, request.Zone, Actor, "master switch", ct);
}
