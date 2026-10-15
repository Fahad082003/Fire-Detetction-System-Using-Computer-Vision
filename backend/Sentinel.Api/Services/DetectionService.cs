using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Validators;

namespace Sentinel.Api.Services;

/// <summary>Entry point for AI inference frames (REST and SignalR share this).</summary>
public class DetectionService(IAlertEngine alerts, IRealtimeNotifier realtime, CameraWatchdog watchdog) : IDetectionService
{
    public async Task<(DetectionFrameDto Frame, int? IncidentId)> ProcessAsync(DetectionPayloadDto payload, CancellationToken ct = default)
    {
        var frame = DetectionPayloadValidator.Validate(payload);
        watchdog.FrameReceived(frame.CameraId);

        // 1) Overlay first: dashboards draw boxes at stream rate, before any database work.
        await realtime.DetectionAsync(frame);

        // 2) Alert routine (returns null for frames that are not alert-worthy).
        var incident = await alerts.EvaluateDetectionAsync(frame, ct);
        return (frame, incident?.Id);
    }
}
