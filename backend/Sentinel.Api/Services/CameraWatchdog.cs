using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>
/// "Camera lost": a camera that was sending frames goes silent (power cut in a fire, cable, crashed AI service).
/// Watched: every camera seen since the API started, plus, from start-up, every camera with a stream link (the cameras the AI
/// service reads), which gets CameraStartupGraceSeconds to send its first frame. Simulated feeds without a link never alarm.
/// CameraFeed.Status follows the same rule: Offline when the silence crosses CameraLostSeconds, Online on fresh frames.
/// </summary>
// shortcut: last-frame times live in this process; with several API instances each one watches only its own frames.
public class CameraWatchdog(IServiceScopeFactory scopes, IRealtimeNotifier realtime, IOptions<AlertOptions> options,
    ILogger<CameraWatchdog> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTime> _lastFrame = new();
    private readonly ConcurrentDictionary<string, byte> _lost = new();
    private readonly ConcurrentDictionary<string, byte> _online = new(); // Status already set to Online by this process
    private readonly AlertOptions _opt = options.Value;

    public void FrameReceived(string cameraId) => _lastFrame[cameraId] = DateTime.UtcNow;

    /// <summary>Start-up reconciliation: a camera that died while the API was down is otherwise never watched, so it would never alarm.</summary>
    public async Task WatchStreamingCamerasAsync(DateTime now, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var ids = await scope.ServiceProvider.GetRequiredService<SentinelDbContext>().CameraFeeds
            .Where(c => c.StreamUrl != null && c.StreamUrl != "").Select(c => c.Id).ToListAsync(ct);
        var seenAt = now.AddSeconds(_opt.CameraStartupGraceSeconds - _opt.CameraLostSeconds); // silent past the grace period -> lost
        foreach (var id in ids) _lastFrame.TryAdd(id, seenAt);                                  // a real frame already received wins
    }

    /// <summary>Raises one alert per silent period and announces when the camera sends again.</summary>
    public async Task CheckAsync(DateTime now, CancellationToken ct = default)
    {
        foreach (var (cameraId, last) in _lastFrame)
        {
            var silentFor = now - last;
            if (silentFor.TotalSeconds > _opt.CameraLostSeconds)
            {
                if (!_lost.TryAdd(cameraId, 0)) continue; // already reported
                _online.TryRemove(cameraId, out _);       // CameraLostAsync stores Offline
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAlertEngine>().CameraLostAsync(cameraId, (int)silentFor.TotalSeconds, ct);
            }
            else
            {
                if (_lost.TryRemove(cameraId, out _))
                    await realtime.ActivityAsync("success", $"Camera {cameraId} is sending frames again.");
                // Once per process and after every loss, so a status left Offline before an API restart is corrected too.
                if (!_online.ContainsKey(cameraId))
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<SentinelDbContext>().CameraFeeds
                        .Where(c => c.Id == cameraId && c.Status != CameraStatus.Online)
                        .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, CameraStatus.Online), ct); // unknown ids: 0 rows
                    _online[cameraId] = 0; // only after the write, so a failed write is retried on the next check
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await WatchStreamingCamerasAsync(DateTime.UtcNow, stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Camera watchdog start-up reconciliation failed"); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CheckAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Camera watchdog check failed"); }
        }
    }
}
