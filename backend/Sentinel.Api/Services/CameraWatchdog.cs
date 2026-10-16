using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Interfaces;

namespace Sentinel.Api.Services;

/// <summary>
/// "Camera lost": a camera that was sending frames goes silent (power cut in a fire, cable, crashed AI service).
/// Only cameras seen since the API started are watched, so a camera that was never connected does not alarm.
/// </summary>
// shortcut: last-frame times live in this process; with several API instances each one watches only its own frames.
public class CameraWatchdog(IServiceScopeFactory scopes, IRealtimeNotifier realtime, IOptions<AlertOptions> options,
    ILogger<CameraWatchdog> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTime> _lastFrame = new();
    private readonly ConcurrentDictionary<string, byte> _lost = new();
    private readonly AlertOptions _opt = options.Value;

    public void FrameReceived(string cameraId) => _lastFrame[cameraId] = DateTime.UtcNow;

    /// <summary>Raises one alert per silent period and announces when the camera sends again.</summary>
    public async Task CheckAsync(DateTime now, CancellationToken ct = default)
    {
        foreach (var (cameraId, last) in _lastFrame)
        {
            var silentFor = now - last;
            if (silentFor.TotalSeconds > _opt.CameraLostSeconds)
            {
                if (!_lost.TryAdd(cameraId, 0)) continue; // already reported
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAlertEngine>().CameraLostAsync(cameraId, (int)silentFor.TotalSeconds, ct);
            }
            else if (_lost.TryRemove(cameraId, out _))
            {
                await realtime.ActivityAsync("success", $"Camera {cameraId} is sending frames again.");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await CheckAsync(DateTime.UtcNow, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Camera watchdog check failed"); }
        }
    }
}
