using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Security;

namespace Sentinel.Api.Hubs;

/// <summary>
/// Real-time channel mapped at <c>/hubs/monitoring</c>.
///  • Dashboards (operator JWT) connect and receive every broadcast event.
///  • AI pipelines / IoT gateways (X-Device-Key) call <see cref="PublishDetection"/> and <see cref="PublishTelemetry"/>.
/// </summary>
[Authorize(Policy = AuthPolicies.OperatorOrDevice)]
public class EmergencyMonitoringHub(
    IDashboardService dashboard,
    IDetectionService detections,
    ISensorService sensors,
    ILogger<EmergencyMonitoringHub> logger) : Hub
{
    private const int MaxMessagesPerSecond = 60;

    public override async Task OnConnectedAsync()
    {
        // Every new client immediately gets the full current state, so the screen is never empty.
        await Clients.Caller.SendAsync(HubEvents.Snapshot, await dashboard.GetSnapshotAsync(Context.ConnectionAborted));
        await base.OnConnectedAsync();
    }

    /// <summary>Re-sync on demand (e.g. after the browser tab wakes up).</summary>
    public Task<DashboardSnapshotDto> GetSnapshot() => dashboard.GetSnapshotAsync(Context.ConnectionAborted);

    /// <summary>
    /// Receives one AI inference frame (bounding boxes, danger zones, fire severity), broadcasts it to all
    /// dashboards and runs the alert routine (incident, SMS/Email/Push, auto-sprinklers).
    /// </summary>
    [Authorize(Policy = AuthPolicies.Device)]
    public async Task<HubResult> PublishDetection(DetectionPayloadDto payload)
    {
        if (IsRateLimited()) return HubResult.Fail("Rate limit exceeded.");
        try
        {
            var (_, incidentId) = await detections.ProcessAsync(payload, Context.ConnectionAborted);
            return HubResult.Success(incidentId);
        }
        catch (AppValidationException ex)
        {
            return HubResult.Fail(ex.Message, ex.Errors);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PublishDetection failed for camera {Camera}", payload?.CameraId);
            return HubResult.Fail("Internal processing error.");
        }
    }

    /// <summary>Receives one sensor reading from an IoT gateway.</summary>
    [Authorize(Policy = AuthPolicies.Device)]
    public async Task<HubResult> PublishTelemetry(TelemetryMessageDto message)
    {
        if (IsRateLimited()) return HubResult.Fail("Rate limit exceeded.");
        try
        {
            await sensors.IngestReadingAsync(message.SensorId, message.Value, message.RecordedAt, Context.ConnectionAborted);
            return HubResult.Success();
        }
        catch (Exception ex) when (ex is NotFoundException or AppValidationException)
        {
            return HubResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PublishTelemetry failed for sensor {Sensor}", message?.SensorId);
            return HubResult.Fail("Internal processing error.");
        }
    }

    private bool IsRateLimited()
    {
        var now = DateTime.UtcNow;
        var window = Context.Items.TryGetValue("rl-window", out var w) ? (DateTime)w! : now;
        var count = Context.Items.TryGetValue("rl-count", out var c) ? (int)c! : 0;
        if ((now - window).TotalSeconds >= 1) { window = now; count = 0; }
        Context.Items["rl-window"] = window;
        Context.Items["rl-count"] = ++count;
        return count > MaxMessagesPerSecond;
    }
}

/// <summary>Acknowledgement returned to producers for every published message.</summary>
public record HubResult(bool Ok, string? Error = null, int? IncidentId = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static HubResult Success(int? incidentId = null) => new(true, IncidentId: incidentId);
    public static HubResult Fail(string error, IReadOnlyDictionary<string, string[]>? errors = null) => new(false, error, Errors: errors);
}
