using Microsoft.AspNetCore.SignalR;
using Sentinel.Api.DTOs;
using Sentinel.Api.Hubs;
using Sentinel.Api.Interfaces;

namespace Sentinel.Api.Services;

/// <summary>Pushes events to every connected dashboard through the SignalR hub context.</summary>
public class RealtimeNotifier(IHubContext<EmergencyMonitoringHub> hub, ILogger<RealtimeNotifier> logger) : IRealtimeNotifier
{
    public Task DetectionAsync(DetectionFrameDto frame) => Send(HubEvents.Detection, frame);
    public Task TelemetryAsync(SensorNodeDto sensor) => Send(HubEvents.Telemetry, sensor);
    public Task SprinklerAsync(SmartSprinklerDto sprinkler) => Send(HubEvents.Sprinkler, sprinkler);
    public Task IncidentAsync(IncidentEventDto evt) => Send(HubEvents.Incident, evt);
    public Task NotificationAsync(NotificationResultDto result) => Send(HubEvents.Notification, result);

    public Task ActivityAsync(string level, string message, string? zone = null) =>
        Send(HubEvents.Activity, new ActivityDto(level, message, zone, DateTime.UtcNow));

    private async Task Send(string evt, object payload)
    {
        try
        {
            await hub.Clients.All.SendAsync(evt, payload);
        }
        catch (Exception ex) // a broadcast failure must never break the alert pipeline
        {
            logger.LogWarning(ex, "Broadcast of {Event} failed", evt);
        }
    }
}
