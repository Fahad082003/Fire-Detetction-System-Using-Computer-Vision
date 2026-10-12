using Microsoft.EntityFrameworkCore;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Incident register: listing, resolving and operator-initiated emergency dispatch.</summary>
public class IncidentService(
    SentinelDbContext db,
    IAlertEngine alerts,
    ISprinklerService sprinklers,
    IRealtimeNotifier realtime) : IIncidentService
{
    public async Task<IReadOnlyList<IncidentAlertDto>> ListAsync(bool? resolved, Severity? minSeverity, int limit, CancellationToken ct = default)
    {
        var query = db.IncidentAlerts.AsNoTracking().Include(i => i.Camera).AsQueryable();
        if (resolved is { } r) query = query.Where(i => i.IsResolved == r);
        if (minSeverity is { } min)
        {
            var allowed = Enum.GetValues<Severity>().Where(s => s >= min).ToList();
            query = query.Where(i => allowed.Contains(i.Severity));
        }
        var items = await query.OrderByDescending(i => i.Timestamp).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        return items.Select(i => i.ToDto()).ToList();
    }

    public async Task<IncidentAlertDto> GetAsync(int id, CancellationToken ct = default) =>
        (await FindAsync(id, ct)).ToDto();

    public async Task<IncidentAlertDto> ResolveAsync(int id, string actor, CancellationToken ct = default)
    {
        var incident = await FindAsync(id, ct);
        if (incident.IsResolved) return incident.ToDto();

        incident.IsResolved = true;
        incident.ResolvedAt = DateTime.UtcNow;
        incident.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var dto = incident.ToDto();
        await realtime.IncidentAsync(new IncidentEventDto("resolved", dto));
        await realtime.ActivityAsync("success", $"Incident #{incident.Id} resolved by {actor}", incident.Zone);
        await alerts.AddLogAsync(new IncidentLog
        {
            IncidentId = incident.Id, Type = IncidentLogType.Resolved, Zone = incident.Zone, Details = $"Resolved by {actor}",
        }, ct);
        return dto;
    }

    public async Task<IReadOnlyList<IncidentLogDto>> ListLogsAsync(int? incidentId, IncidentLogType? type, int limit, CancellationToken ct = default)
    {
        var query = db.IncidentLogs.AsNoTracking();
        if (incidentId is { } id) query = query.Where(l => l.IncidentId == id);
        if (type is { } t) query = query.Where(l => l.Type == t);
        var items = await query.OrderByDescending(l => l.Timestamp).ThenByDescending(l => l.Id)
            .Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);
        return items.Select(l => l.ToDto()).ToList();
    }

    public async Task<IncidentAlertDto> DispatchAsync(ManualDispatchRequest request, string actor, CancellationToken ct = default)
    {
        var incident = new IncidentAlert
        {
            Source = IncidentSource.Manual,
            Zone = request.Zone.Trim(),
            Severity = request.Severity,
            Message = string.IsNullOrWhiteSpace(request.Message) ? $"Manual emergency dispatch initiated by {actor}" : request.Message.Trim(),
        };
        db.IncidentAlerts.Add(incident);
        await db.SaveChangesAsync(ct);

        // forceNotify bypasses the cooldown: an operator pressing the button always reaches responders.
        await alerts.PublishIncidentAsync(incident, "created", emergency: false, forceNotify: true, ct);
        if (request.ActivateSprinklers)
            await sprinklers.SetAllAsync(true, incident.Zone, actor, $"dispatch #{incident.Id}", ct);
        return incident.ToDto();
    }

    private async Task<IncidentAlert> FindAsync(int id, CancellationToken ct) =>
        await db.IncidentAlerts.Include(i => i.Camera).FirstOrDefaultAsync(i => i.Id == id, ct)
        ?? throw new NotFoundException($"Incident #{id} not found.");
}
