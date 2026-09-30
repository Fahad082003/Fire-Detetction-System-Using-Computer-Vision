using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Builds the full dashboard state in one call (initial load and SignalR reconnects).</summary>
public class DashboardService(SentinelDbContext db, IOptions<AlertOptions> options) : IDashboardService
{
    private readonly AlertOptions _opt = options.Value;

    public async Task<DashboardSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
    {
        var cameras = await db.CameraFeeds.AsNoTracking().OrderBy(c => c.FeedType).ThenBy(c => c.Name).ToListAsync(ct);
        var sensors = await db.SensorNodes.AsNoTracking().OrderBy(s => s.LocationZone).ThenBy(s => s.Type).ToListAsync(ct);
        var sprinklers = await db.SmartSprinklers.AsNoTracking().OrderBy(s => s.Zone).ThenBy(s => s.Id).ToListAsync(ct);
        var incidents = await db.IncidentAlerts.AsNoTracking().Include(i => i.Camera)
            .OrderByDescending(i => i.Timestamp).Take(50).ToListAsync(ct);

        var sensorDtos = new List<SensorNodeDto>(sensors.Count);
        foreach (var s in sensors)
        {
            var history = await db.SensorReadings.AsNoTracking().Where(r => r.SensorId == s.Id)
                .OrderByDescending(r => r.RecordedAt).Take(_opt.SensorHistoryPoints).Select(r => r.Value).ToListAsync(ct);
            history.Reverse();
            sensorDtos.Add(s.ToDto(_opt, history));
        }

        var openSeverities = await db.IncidentAlerts.AsNoTracking().Where(i => !i.IsResolved).Select(i => i.Severity).ToListAsync(ct);
        var states = sensors.Select(s => SensorRules.StateOf(s, _opt)).ToHashSet();
        var level =
            openSeverities.Any(s => s >= Severity.High) || states.Contains(SensorState.Alarm) || states.Contains(SensorState.Critical) ? "critical"
            : openSeverities.Count > 0 || states.Contains(SensorState.Warning) ? "elevated"
            : "nominal";

        return new DashboardSnapshotDto(
            cameras.Select(c => c.ToDto()).ToList(),
            sensorDtos,
            sprinklers.Select(s => s.ToDto()).ToList(),
            incidents.Select(i => i.ToDto()).ToList(),
            new SystemStatusDto(level, openSeverities.Count, sprinklers.Count(s => s.IsActive)),
            DateTime.UtcNow);
    }
}
