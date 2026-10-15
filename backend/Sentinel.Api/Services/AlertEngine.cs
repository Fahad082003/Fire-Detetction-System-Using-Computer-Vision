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
        if (!f.FireDetected && f.DangerCount == 0 && !f.SmokeDetected) return null;

        var severity = f.SmokeDetected ? Severity.Medium : Severity.Low; // smoke without flames still needs an operator
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
        if (frame.SmokeDetected) parts.Add("Smoke detected");
        var dangerZones = frame.Detections.Where(d => d.DangerZone is not null).Select(d => d.DangerZone).Distinct().ToList();
        if (frame.DangerCount > 0)
            parts.Add($"{frame.DangerCount} human(s) in danger zone{(dangerZones.Count > 0 ? $" '{string.Join("', '", dangerZones)}'" : "")}");
        if (frame.HumanCount > 0) parts.Add($"{frame.HumanCount} human(s) in view");
        var cameraName = camera?.Name ?? frame.CameraId;
        var simulated = (frame.Model is null ? "" : $" [AI {frame.Model}]") + (frame.Simulated ? " [SIMULATED DATA]" : "");

        var (incident, action) = await UpsertIncidentAsync(new IncidentDraft(
            IncidentSource.Vision, zone, severity.Value, camera?.Id, null, frame.FireDetected, frame.FireSeverity,
            frame.HumanCount, frame.DangerCount, $"{string.Join(", ", parts)} on {cameraName}{simulated}"), ct);

        var emergency = IsEmergency(frame) || severity == Severity.Critical;
        await PublishIncidentAsync(incident, action, emergency, ct: ct, simulated: frame.Simulated);
        await LogFrameAsync(incident, frame, cameraName, ct);
        return incident;
    }

    /// <summary>Requirement 3.2 / 2.3: fire-measurement history and one alert per person per danger zone per incident.</summary>
    private async Task LogFrameAsync(IncidentAlert incident, DetectionFrameDto f, string cameraName, CancellationToken ct)
    {
        var sim = f.Simulated ? " [SIMULATED DATA]" : "";
        if (f.FireDetected)
        {
            // Measured value = AI fire bounding boxes as % of the frame. It is not a physical fire size.
            var frameArea = f.Normalized ? 1.0 : (double)f.FrameWidth * f.FrameHeight;
            var area = Math.Round(Math.Min(100, f.Detections.Where(d => d.Category == DetectionCategory.Fire)
                .Sum(d => d.Bbox[2] * d.Bbox[3]) / frameArea * 100), 2);
            var last = await db.IncidentLogs.Where(l => l.IncidentId == incident.Id && l.Type == IncidentLogType.FireMeasurement)
                .OrderByDescending(l => l.Id).FirstOrDefaultAsync(ct);
            if (last is null || last.FireAreaPercent != area || last.FireSeverity != f.FireSeverity
                || last.HumanCount != f.HumanCount || last.DangerCount != f.DangerCount)
            {
                await AddLogAsync(new IncidentLog
                {
                    IncidentId = incident.Id, Type = IncidentLogType.FireMeasurement, Zone = incident.Zone,
                    FireAreaPercent = area, FireSeverity = f.FireSeverity, HumanCount = f.HumanCount, DangerCount = f.DangerCount,
                    Simulated = f.Simulated,
                    Details = $"Fire boxes cover {area}% of the frame on {cameraName}, AI fire severity {f.FireSeverity?.ToString() ?? "n/a"}{sim}",
                }, ct);
            }
        }

        foreach (var d in f.Detections.Where(d => d.Category == DetectionCategory.Human && d.AtRisk))
        {
            var exists = await db.IncidentLogs.AnyAsync(l => l.IncidentId == incident.Id && l.Type == IncidentLogType.PersonInDanger
                && l.PersonTrackId == d.TrackId && l.DangerZone == d.DangerZone, ct);
            if (exists) continue; // same person + same zone + same incident = one alert

            double fx = Math.Round(d.Bbox[0] + d.Bbox[2] / 2, 1), fy = Math.Round(d.Bbox[1] + d.Bbox[3], 1);
            var who = d.TrackId is null ? "Untracked person" : $"Person #{d.TrackId}";
            var where = d.DangerZone is null ? "a danger zone (flagged by AI)" : $"danger zone '{d.DangerZone}'";
            var details = $"{who} in {where} at ({fx}, {fy}) on {cameraName}{sim}";
            await AddLogAsync(new IncidentLog
            {
                IncidentId = incident.Id, Type = IncidentLogType.PersonInDanger, Zone = incident.Zone, Details = details,
                PersonTrackId = d.TrackId, PersonX = fx, PersonY = fy, DangerZone = d.DangerZone, Simulated = f.Simulated,
            }, ct);
            await realtime.ActivityAsync("critical", $"Incident #{incident.Id}: {details}", incident.Zone);
        }
    }

    public async Task AddLogAsync(IncidentLog log, CancellationToken ct = default)
    {
        if (log.Details.Length > 500) log.Details = log.Details[..500];
        db.IncidentLogs.Add(log);
        await db.SaveChangesAsync(ct);
        await realtime.IncidentLogAsync(log.ToDto());
    }

    // ------------------------------------------------------------------ sensors
    public async Task EvaluateSensorAsync(SensorNode sensor, SensorState previousState, bool simulated = false, CancellationToken ct = default)
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
            $"{sensor.Type} {sensor.CurrentValue}{sensor.Unit} exceeds safety limit {sensor.Threshold}{sensor.Unit} ({sensor.Id})"
            + (simulated ? " [SIMULATED DATA]" : "")), ct);

        await PublishIncidentAsync(incident, action, emergency: state == SensorState.Critical, ct: ct, simulated: simulated);
        // A reading over the safety limit opens the zone's Auto relays even before it becomes Critical, but test data never does.
        if (!simulated || _opt.AutoSprinklerOnSimulatedData)
            await sprinklers.AutoActivateZoneAsync(sensor.LocationZone, $"{sensor.Id} {state}", ct);
        else if (action is "created" or "escalated" && state != SensorState.Critical) // Critical: PublishIncidentAsync already said so
            await realtime.ActivityAsync("warning",
                $"Incident #{incident.Id}: sprinkler auto-activation skipped, the triggering data is SIMULATED (Alerts:AutoSprinklerOnSimulatedData=false)", incident.Zone);
    }

    // ------------------------------------------------------------------ camera health
    public async Task CameraLostAsync(string cameraId, int silentSeconds, CancellationToken ct = default)
    {
        var camera = await db.CameraFeeds.FindAsync([cameraId], ct);
        if (camera is null) return;
        // Losing a camera matters most while there is a fire in its zone: that is when power cuts happen.
        var fireOpen = await db.IncidentAlerts.AnyAsync(i => !i.IsResolved && i.Zone == camera.LocationZone && i.FireDetected, ct);
        var (incident, action) = await UpsertIncidentAsync(new IncidentDraft(
            IncidentSource.CameraLost, camera.LocationZone, fireOpen ? Severity.High : Severity.Medium, camera.Id, null, false, null, 0, 0,
            $"Camera lost: no frames from {camera.Name} for {silentSeconds} s" + (fireOpen ? " while a fire incident is open in this zone" : "")), ct);
        await PublishIncidentAsync(incident, action, emergency: false, ct: ct);
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

    public async Task PublishIncidentAsync(IncidentAlert incident, string action, bool emergency, bool forceNotify = false,
        CancellationToken ct = default, bool simulated = false)
    {
        await realtime.IncidentAsync(new IncidentEventDto(action, incident.ToDto()));
        if (action is not ("created" or "escalated")) return;

        await AddLogAsync(new IncidentLog
        {
            IncidentId = incident.Id, Type = action == "created" ? IncidentLogType.Opened : IncidentLogType.Escalated,
            Zone = incident.Zone, Details = $"{incident.Severity}: {incident.Message}", FireSeverity = incident.FireSeverity,
            HumanCount = incident.HumanCount, DangerCount = incident.DangerCount, Simulated = simulated,
        }, ct);

        var level = incident.Severity >= Severity.High ? "critical" : "warning";
        await realtime.ActivityAsync(level,
            $"Incident #{incident.Id} {(action == "created" ? "opened" : "escalated to")} {incident.Severity}: {incident.Message}", incident.Zone);

        if (emergency || forceNotify)
        {
            if (dispatcher.Enqueue(incident, forceNotify))
                logger.LogInformation("Emergency notifications queued for incident #{Id}", incident.Id);
        }
        if (emergency && simulated && !_opt.AutoSprinklerOnSimulatedData)
            await realtime.ActivityAsync("warning",
                $"Incident #{incident.Id}: sprinkler auto-activation skipped, the triggering data is SIMULATED (Alerts:AutoSprinklerOnSimulatedData=false)", incident.Zone);
        else if (emergency)
            await sprinklers.AutoActivateZoneAsync(incident.Zone, $"emergency incident #{incident.Id}", ct);
    }
}
