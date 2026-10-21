using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;
using Sentinel.Api.Services;

namespace Sentinel.Api.Tests;

/// <summary>Own fixture (fresh database): CameraFeed.Status follows real frames and the existing 10 s camera-lost rule.</summary>
public class CameraStatusTests(SentinelApiFactory factory) : IClassFixture<SentinelApiFactory>
{
    private async Task<CameraStatus> Status(string id) =>
        (await (await factory.OperatorClientAsync()).GetFromJsonAsync<CameraFeedDto>($"/api/cameras/{id}", TestJson.Options))!.Status;

    private async Task SetStatus(string id, CameraStatus status)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SentinelDbContext>().CameraFeeds.Where(c => c.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, status));
    }

    private Task<HttpResponseMessage> Frame(string id) => factory.DeviceClient().PostAsJsonAsync("/api/detections",
        new { cameraId = id, frameWidth = 640, frameHeight = 480, detections = Array.Empty<object>() });

    [Fact]
    public async Task Status_goes_online_on_frames_offline_after_the_lost_timeout_and_back_online_on_reconnect()
    {
        var watchdog = factory.Services.GetRequiredService<CameraWatchdog>();
        await SetStatus("cam-lobby", CameraStatus.Offline);              // e.g. left over from before
        Assert.Equal(HttpStatusCode.Accepted, (await Frame("cam-lobby")).StatusCode);
        await watchdog.CheckAsync(DateTime.UtcNow);
        Assert.Equal(CameraStatus.Online, await Status("cam-lobby"));    // valid frames -> Online

        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(CameraStatus.Online, await Status("cam-lobby"));    // one late frame is not a lost camera

        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(CameraStatus.Offline, await Status("cam-lobby"));   // past CameraLostSeconds -> Offline (with the incident)

        await Frame("cam-lobby");
        await watchdog.CheckAsync(DateTime.UtcNow);
        Assert.Equal(CameraStatus.Online, await Status("cam-lobby"));    // reconnect -> Online
        var lost = await (await factory.OperatorClientAsync()).GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents?resolved=false", TestJson.Options);
        Assert.Contains(lost!, i => i.CameraId == "cam-lobby" && i.Source == IncidentSource.CameraLost); // reconnect does not close the incident
    }

    [Fact]
    public async Task After_an_api_restart_a_stale_offline_status_is_corrected_by_the_first_frames()
    {
        await SetStatus("cam-server", CameraStatus.Offline);
        var restarted = new CameraWatchdog(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<IRealtimeNotifier>(),
            factory.Services.GetRequiredService<IOptions<AlertOptions>>(), factory.Services.GetRequiredService<ILogger<CameraWatchdog>>()); // a new process: nothing remembered

        await restarted.CheckAsync(DateTime.UtcNow);
        Assert.Equal(CameraStatus.Offline, await Status("cam-server")); // no frames yet: last known status kept

        restarted.FrameReceived("cam-server");
        await restarted.CheckAsync(DateTime.UtcNow);
        Assert.Equal(CameraStatus.Online, await Status("cam-server"));
    }

    [Fact]
    public async Task After_an_api_restart_a_streaming_camera_that_never_returns_is_reported_lost_after_the_grace_period()
    {
        var admin = await factory.OperatorClientAsync();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/cameras", new { id = "cam-dead", name = "Dead", feedType = "CCTV",
            streamUrl = "rtsp://10.0.0.99/live", streamFormat = "Simulated", locationZone = "Ground Floor - Store" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/cameras", new { id = "cam-alive", name = "Alive", feedType = "CCTV",
            streamUrl = "rtsp://10.0.0.98/live", streamFormat = "Simulated", locationZone = "Ground Floor - Garage" })).StatusCode);
        var restarted = new CameraWatchdog(factory.Services.GetRequiredService<IServiceScopeFactory>(), factory.Services.GetRequiredService<IRealtimeNotifier>(),
            factory.Services.GetRequiredService<IOptions<AlertOptions>>(), factory.Services.GetRequiredService<ILogger<CameraWatchdog>>());
        restarted.FrameReceived("cam-alive");                            // its stream came back before reconciliation: the real frame wins
        var start = DateTime.UtcNow;
        await restarted.WatchStreamingCamerasAsync(start);

        await restarted.CheckAsync(start.AddSeconds(5));                 // cam-alive is fresh; cam-dead is inside the grace period
        await restarted.CheckAsync(start.AddSeconds(30));
        Assert.DoesNotContain(await LostCameras(), id => id == "cam-dead");

        await restarted.CheckAsync(start.AddSeconds(61));                // grace (60 s) over, still no frame
        var lost = await LostCameras();
        Assert.Contains("cam-dead", lost);
        Assert.Equal(CameraStatus.Offline, await Status("cam-dead"));
        Assert.DoesNotContain("cam-lobby", lost);                        // demo camera without a stream link: never watched at start-up
    }

    private async Task<List<string?>> LostCameras() =>
        (await (await factory.OperatorClientAsync()).GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents?resolved=false", TestJson.Options))!
            .Where(i => i.Source == IncidentSource.CameraLost).Select(i => i.CameraId).ToList();

    [Fact]
    public async Task Frames_from_an_unknown_camera_change_nothing_and_do_not_fail()
    {
        var watchdog = factory.Services.GetRequiredService<CameraWatchdog>();
        watchdog.FrameReceived("cam-not-registered");
        await watchdog.CheckAsync(DateTime.UtcNow);                       // 0 rows updated, no exception
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(31));        // CameraLostAsync ignores unknown ids
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<SentinelDbContext>().CameraFeeds.AnyAsync(c => c.Id == "cam-not-registered"));
    }
}
