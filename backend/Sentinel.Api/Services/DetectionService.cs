using Microsoft.Extensions.Caching.Memory;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Validators;

namespace Sentinel.Api.Services;

/// <summary>Entry point for AI inference frames (REST and SignalR share this).</summary>
public class DetectionService(IAlertEngine alerts, IRealtimeNotifier realtime, CameraWatchdog watchdog, IMemoryCache cache) : IDetectionService
{
    public async Task<(DetectionFrameDto Frame, int? IncidentId)> ProcessAsync(DetectionPayloadDto payload, CancellationToken ct = default)
    {
        var frame = DetectionPayloadValidator.Validate(payload);
        if (cache.TryGetValue(CameraService.DeletedKey(frame.CameraId), out _)) // 404 tells the AI service to stop this camera
            throw new NotFoundException($"Camera '{frame.CameraId}' was deleted; frame ignored.");
        watchdog.FrameReceived(frame.CameraId);

        // 1) Overlay first: dashboards draw boxes at stream rate, before any database work.
        await realtime.DetectionAsync(frame);

        // 2) Alert routine (returns null for frames that are not alert-worthy).
        var incident = await alerts.EvaluateDetectionAsync(frame, ct);
        return (frame, incident?.Id);
    }
}
