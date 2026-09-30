using Microsoft.EntityFrameworkCore;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Smart sprinkler relay control: manual toggle, master switch and automatic activation.</summary>
public class SprinklerService(SentinelDbContext db, IRealtimeNotifier realtime) : ISprinklerService
{
    public async Task<IReadOnlyList<SmartSprinklerDto>> GetAllAsync(CancellationToken ct = default) =>
        (await db.SmartSprinklers.AsNoTracking().OrderBy(s => s.Zone).ThenBy(s => s.Id).ToListAsync(ct))
        .Select(s => s.ToDto()).ToList();

    public async Task<SmartSprinklerDto> ToggleAsync(string id, ToggleSprinklerRequest request, string actor, CancellationToken ct = default)
    {
        var sprinkler = await db.SmartSprinklers.FindAsync([id], ct) ?? throw new NotFoundException($"Sprinkler '{id}' not found.");
        var isActive = request.IsActive;
        if (isActive is null && request.Mode is null) isActive = !sprinkler.IsActive; // empty body = flip
        await ApplyAsync(sprinkler, isActive, request.Mode, actor, reason: null, ct);
        return sprinkler.ToDto();
    }

    public async Task<MasterSprinklerResult> SetAllAsync(bool isActive, string? zone, string actor, string reason, CancellationToken ct = default)
    {
        var query = db.SmartSprinklers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(zone)) query = query.Where(s => s.Zone == zone);
        var changed = new List<string>();
        foreach (var s in await query.ToListAsync(ct))
            if (await ApplyAsync(s, isActive, null, actor, reason, ct)) changed.Add(s.Id);

        await realtime.ActivityAsync(isActive ? "critical" : "info",
            $"Master sprinkler switch → {(isActive ? "ON" : "OFF")} ({(string.IsNullOrWhiteSpace(zone) ? "all zones" : zone)}, {changed.Count} relay(s)) by {actor}", zone);
        return new MasterSprinklerResult(changed, await GetAllAsync(ct));
    }

    public async Task<IReadOnlyList<string>> AutoActivateZoneAsync(string zone, string reason, CancellationToken ct = default)
    {
        var targets = await db.SmartSprinklers
            .Where(s => s.Zone == zone && s.Mode == SprinklerMode.Auto && !s.IsActive)
            .ToListAsync(ct);
        var changed = new List<string>();
        foreach (var s in targets)
            if (await ApplyAsync(s, true, null, "auto-engine", reason, ct)) changed.Add(s.Id);
        return changed;
    }

    private async Task<bool> ApplyAsync(SmartSprinkler s, bool? isActive, SprinklerMode? mode, string actor, string? reason, CancellationToken ct)
    {
        var statusChanged = isActive is { } a && a != s.IsActive;
        var modeChanged = mode is { } m && m != s.Mode;
        if (!statusChanged && !modeChanged) return false;

        if (modeChanged) s.Mode = mode!.Value;
        if (statusChanged)
        {
            s.IsActive = isActive!.Value;
            if (s.IsActive) s.LastTriggeredAt = DateTime.UtcNow;
        }
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await realtime.SprinklerAsync(s.ToDto());
        var why = string.IsNullOrEmpty(reason) ? "" : $" — {reason}";
        if (statusChanged)
            await realtime.ActivityAsync(s.IsActive ? "warning" : "info",
                $"Sprinkler {s.Id} {(s.IsActive ? "ACTIVATED" : "deactivated")} by {actor}{why}", s.Zone);
        else
            await realtime.ActivityAsync("info", $"Sprinkler {s.Id} set to {s.Mode} mode by {actor}", s.Zone);
        return true;
    }
}
