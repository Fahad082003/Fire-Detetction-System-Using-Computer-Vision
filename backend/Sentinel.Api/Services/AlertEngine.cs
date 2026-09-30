using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Per-origin async locks so two frames from the same camera never create duplicate incidents.</summary>
public class IncidentLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public SemaphoreSlim For(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
}

public record IncidentDraft(
    IncidentSource Source, string Zone, Severity Severity, string? CameraId, string? SensorId,
    bool FireDetected, Severity? FireSeverity, int HumanCount, int DangerCount, string Message);

/// <summary>
/// The brain of the system. Decides severity, opens/merges/escalates incidents, and triggers the
/// emergency response (SMS + Email + Push notifications and automatic sprinkler activation).
/// </summary>
public class AlertEngine(
    SentinelDbContext db,
    IOptions<AlertOptions> options,
    IMemoryCache cache,
    IncidentLocks locks,
    ISprinklerService sprinklers,
    INotificationDispatcher dispatcher,
    IRealtimeNotifier realtime,
    ILogger<AlertEngine> logger) : IAlertEngine
{
    private readonly AlertOptions _opt = options.Value;

    // ------------------------------------------------------------------ rules (pure, unit-tested)

    /// <summary>Severity of an AI frame, or null when nothing is alert-worthy.</summary>
    public static Severity? ClassifyDetection(DetectionFrameDto f)
    {
        if (!f.FireDetected && f.DangerCount == 0) return null;

        var severity = Severity.Low;
        if (f.FireDetected)
        {
            severity = f.FireSeverity ?? Severity.Medium;
            if (f.HumanCount > 0) severity = Max(severity, Severity.High);   // people near an active fire
        }
        if (f.DangerCount > 0) severity = Max(severity, f.DangerCount >= 3 ? Severity.Critical : Severity.High);
        if (f.FireDetected && f.DangerCount > 0) severity = Severity.Critical; // humans inside a fire danger zone
        return severity;
    }

    /// <summary>The emergency trigger: humans in danger, or the AI rates the fire Critical.</summary>
    public static bool IsEmergency(DetectionFrameDto f) =>
        f.DangerCount > 0 || (f.FireDetected && f.FireSeverity == Severity.Critical);

    private static Severity Max(Severity a, Severity b) => a >= b ? a : b;

    // ------------------------------------------------------------------ vision
    public async Task<IncidentAlert?> EvaluateDetectionAsync(DetectionFrameDto frame, CancellationToken ct = default)
    {
        var severity = ClassifyDetection(frame);
        if (severity is null) return null;

        // Frames arrive ~5/s per camera: re-evaluate at most every N seconds unless severity rises.
        var throttleKey = $"vision-eval:{frame.CameraId}";
        if (cache.TryGetValue(throttleKey, out Severity last) && severity <= last) return null;
        cache.Set(throttleKey, severity.Value, TimeSpan.FromSeconds(_opt.VisionEvaluationIntervalSeconds));

        var camera = await db.CameraFeeds.FindAsync([frame.CameraId], ct);
        var zone = camera?.LocationZone ?? "Unassigned";
        var parts = new List<string>();
        if (frame.FireDetected) parts.Add(frame.FireSeverity is { } fs ? $"Fire detected ({fs})" : "Fire detected");
        if (frame.DangerCount > 0) parts.Add($"{frame.DangerCount} human(s) in danger zone");
        if (frame.HumanCount > 0) parts.Add($"{frame.HumanCount} human(s) in view");

        var (incident, action) = await UpsertIncidentAsync(new IncidentDraft(
            IncidentSource.Vision, zone, severity.Value, camera?.Id, null, frame.FireDetected, frame.FireSeverity,
            frame.HumanCount, frame.DangerCount, $"{string.Join(", ", parts)} on {camera?.Name ?? frame.CameraId}"), ct);

        var emergency = IsEmergency(frame) || severity == Severity.Critical;
        await PublishIncidentAsync(incident, action, emergency, ct: ct);
        return incident;
    }

    // ------------------------------------------------------------------ sensors
    public async Task EvaluateSensorAsync(SensorNode sensor, SensorState previousState, CancellationToken ct = default)
    {
        var state = SensorRules.StateOf(sensor, _opt);
        if (state != previousState)
        {
            var level = state switch { SensorState.Normal => "success", SensorState.Warning => "warning", _ => "critical" };
            await realtime.ActivityAsync(level,
                $"{sensor.Type} sensor {sensor.Id}: {previousState} → {state} ({sensor.CurrentValue}{sensor.Unit})", sensor.LocationZone);
        }

        if (state is not (SensorState.Alarm or SensorState.Critical)) return;

        var severity = state == SensorState.Critical ? Severity.Critical : Severity.High;
        var (incident, action) = await UpsertIncidentAsync(new IncidentDraft(
            IncidentSource.Sensor, sensor.LocationZone, severity, null, sensor.Id, false, null, 0, 0,
            $"{sensor.Type} {sensor.CurrentValue}{sensor.Unit} exceeds safety limit {sensor.Threshold}{sensor.Unit} ({sensor.Id})"), ct);

        await PublishIncidentAsync(incident, action, emergency: state == SensorState.Critical, ct: ct);
        // A reading over the safety limit opens the zone's Auto relays even before it becomes Critical.
        await sprinklers.AutoActivateZoneAsync(sensor.LocationZone, $"{sensor.Id} {state}", ct);
    }

    // ------------------------------------------------------------------ incidents
    /// <summary>
    /// Merge into the open incident of the same origin if it was touched within the merge window,
    /// otherwise create a new one. Returns the incident and "created" | "escalated" | "updated".
    /// </summary>
    public async Task<(IncidentAlert Incident, string Action)> UpsertIncidentAsync(IncidentDraft d, CancellationToken ct = default)
    {
        var gate = locks.For($"{d.Source}:{d.Zone}:{d.CameraId ?? d.SensorId ?? "-"}");
        await gate.WaitAsync(ct);
        try
        {
            var windowStart = DateTime.UtcNow.AddSeconds(-_opt.IncidentMergeWindowSeconds);
            var query = db.IncidentAlerts.Include(i => i.Camera)
                .Where(i => !i.IsResolved && i.Source == d.Source && i.Zone == d.Zone && i.UpdatedAt >= windowStart);
            if (d.CameraId is not null) query = query.Where(i => i.CameraId == d.CameraId);
            if (d.SensorId is not null) query = query.Where(i => i.SensorId == d.SensorId);
            var incident = await query.OrderByDescending(i => i.UpdatedAt).FirstOrDefaultAsync(ct);

            var message = d.Message.Length > 500 ? d.Message[..500] : d.Message;
            if (incident is null)
            {
                incident = new IncidentAlert
                {
                    Source = d.Source, Zone = d.Zone, Severity = d.Severity, CameraId = d.CameraId, SensorId = d.SensorId,
                    FireDetected = d.FireDetected, FireSeverity = d.FireSeverity, HumanCount = d.HumanCount,
                    DangerCount = d.DangerCount, Message = message,
                };
                db.IncidentAlerts.Add(incident);
                await db.SaveChangesAsync(ct);
                if (incident.CameraId is not null) await db.Entry(incident).Reference(i => i.Camera).LoadAsync(ct);
                return (incident, "created");
            }

            var escalated = d.Severity > incident.Severity;
            if (escalated)
            {
                incident.Severity = d.Severity;
                incident.Message = message;
            }
            incident.FireDetected |= d.FireDetected;
            if (d.FireSeverity is { } fs && (incident.FireSeverity is null || fs > incident.FireSeverity)) incident.FireSeverity = fs;
            incident.HumanCount = Math.Max(incident.HumanCount, d.HumanCount);
            incident.DangerCount = Math.Max(incident.DangerCount, d.DangerCount);
            incident.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return (incident, escalated ? "escalated" : "updated");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PublishIncidentAsync(IncidentAlert incident, string action, bool emergency, bool forceNotify = false, CancellationToken ct = default)
    {
        await realtime.IncidentAsync(new IncidentEventDto(action, incident.ToDto()));
        if (action is not ("created" or "escalated")) return;

        var level = incident.Severity >= Severity.High ? "critical" : "warning";
        await realtime.ActivityAsync(level,
            $"Incident #{incident.Id} {(action == "created" ? "opened" : "escalated to")} {incident.Severity}: {incident.Message}", incident.Zone);

        if (emergency || forceNotify)
        {
            if (dispatcher.Enqueue(incident, forceNotify))
                logger.LogInformation("Emergency notifications queued for incident #{Id}", incident.Id);
        }
        if (emergency)
            await sprinklers.AutoActivateZoneAsync(incident.Zone, $"emergency incident #{incident.Id}", ct);
    }
}
