using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Smoke / temperature telemetry: stores readings, broadcasts them and hands them to the alert engine.</summary>
public class SensorService(
    SentinelDbContext db,
    IOptions<AlertOptions> options,
    IMemoryCache cache,
    IAlertEngine alerts,
    IRealtimeNotifier realtime,
    ILogger<SensorService> logger) : ISensorService
{
    private const double MinValue = -100, MaxValue = 100_000;
    private readonly AlertOptions _opt = options.Value;

    public async Task<IReadOnlyList<SensorNodeDto>> GetAllAsync(CancellationToken ct = default) =>
        (await db.SensorNodes.AsNoTracking().OrderBy(s => s.LocationZone).ThenBy(s => s.Type).ThenBy(s => s.Id).ToListAsync(ct))
        .Select(s => s.ToDto(_opt)).ToList();

    public async Task<SensorNodeDto> GetAsync(string id, CancellationToken ct = default) =>
        (await FindAsync(id, ct)).ToDto(_opt);

    public async Task<SensorHistoryDto> GetHistoryAsync(string id, int limit, DateTime? since, CancellationToken ct = default)
    {
        var sensor = await FindAsync(id, ct);
        limit = Math.Clamp(limit, 1, 2000);
        var query = db.SensorReadings.AsNoTracking().Where(r => r.SensorId == id);
        if (since is { } s) query = query.Where(r => r.RecordedAt >= s);
        var readings = await query.OrderByDescending(r => r.RecordedAt).Take(limit)
            .Select(r => new SensorReadingDto(r.Value, r.RecordedAt)).ToListAsync(ct);
        readings.Reverse();
        return new SensorHistoryDto(sensor.Id, sensor.Unit, readings);
    }

    public async Task<SensorNodeDto> IngestReadingAsync(string id, double value, DateTime? recordedAt, CancellationToken ct = default)
    {
        if (!double.IsFinite(value) || value is < MinValue or > MaxValue)
            throw new AppValidationException("value", $"Value must be between {MinValue} and {MaxValue}.");

        var sensor = await FindAsync(id, ct);
        var previous = SensorRules.StateOf(sensor, _opt);
        var at = recordedAt?.ToUniversalTime() ?? DateTime.UtcNow;

        sensor.CurrentValue = Math.Round(value, 2);
        sensor.LastReadingAt = at;
        sensor.UpdatedAt = DateTime.UtcNow;
        db.SensorReadings.Add(new SensorReading { SensorId = sensor.Id, Value = sensor.CurrentValue, RecordedAt = at });
        await db.SaveChangesAsync(ct);
        await PruneOldReadingsAsync(ct);

        var dto = sensor.ToDto(_opt);
        await realtime.TelemetryAsync(dto);
        await alerts.EvaluateSensorAsync(sensor, previous, ct);
        return dto;
    }

    public async Task<BulkTelemetryResult> IngestBulkAsync(IEnumerable<BulkTelemetryItem> readings, CancellationToken ct = default)
    {
        var updated = new List<string>();
        var errors = new List<TelemetryError>();
        foreach (var r in readings)
        {
            try
            {
                await IngestReadingAsync(r.SensorId, r.Value, null, ct);
                updated.Add(r.SensorId);
            }
            catch (Exception ex) when (ex is NotFoundException or AppValidationException)
            {
                errors.Add(new TelemetryError(r.SensorId, ex.Message));
            }
        }
        return new BulkTelemetryResult(updated, errors);
    }

    private async Task<SensorNode> FindAsync(string id, CancellationToken ct) =>
        await db.SensorNodes.FindAsync([id], ct) ?? throw new NotFoundException($"Sensor '{id}' not found.");

    /// <summary>Keeps the readings table small: deletes history older than the retention window, at most every 10 minutes.</summary>
    private async Task PruneOldReadingsAsync(CancellationToken ct)
    {
        if (cache.TryGetValue("prune-readings", out _)) return;
        cache.Set("prune-readings", true, TimeSpan.FromMinutes(10));
        var cutoff = DateTime.UtcNow.AddHours(-_opt.SensorHistoryRetentionHours);
        var deleted = await db.SensorReadings.Where(r => r.RecordedAt < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0) logger.LogInformation("Pruned {Count} sensor readings older than {Cutoff}", deleted, cutoff);
    }
}
