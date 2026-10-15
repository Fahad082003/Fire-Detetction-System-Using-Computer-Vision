using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Api.DTOs;
using Sentinel.Api.Models;
using Sentinel.Api.Services;

namespace Sentinel.Api.Tests;

/// <summary>Own fixture (fresh database): these tests assert sprinkler and incident state that other test classes change.</summary>
public class CameraLostAndSimulatedTests(SentinelApiFactory factory) : IClassFixture<SentinelApiFactory>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = TestJson.Options;

    [Fact]
    public async Task Simulated_sensor_alarm_is_labelled_and_never_opens_sprinklers()
    {
        var device = factory.DeviceClient();
        (await device.PostAsJsonAsync("/api/iot/telemetry", new { readings = new[] { new { sensorId = "temp-b1", value = 80.0 } }, simulated = true })).EnsureSuccessStatusCode();

        var op = await factory.OperatorClientAsync();
        var incident = (await op.GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents", Json))!.Single(i => i.SensorId == "temp-b1");
        Assert.Equal(Severity.Critical, incident.Severity);
        Assert.Contains("[SIMULATED DATA]", incident.Message);
        Assert.DoesNotContain(await Sprinklers(device), s => s.Zone == "Zone B" && s.IsActive);

        // Control: the same reading without the flag is real data and opens the zone's Auto relays.
        (await device.PostAsJsonAsync("/api/iot/telemetry", new { readings = new[] { new { sensorId = "temp-b1", value = 81.0 } } })).EnsureSuccessStatusCode();
        Assert.Contains(await Sprinklers(device), s => s.Zone == "Zone B" && s.IsActive);
    }

    [Fact]
    public async Task Silent_camera_raises_one_camera_lost_alert_high_while_its_zone_burns()
    {
        var device = factory.DeviceClient();
        // cam-server (Zone C) reports a fire; cam-lobby (Zone A) sends a quiet frame. drone-01 never sends anything.
        (await device.PostAsJsonAsync("/api/detections", new { cameraId = "cam-server", frameWidth = 1280, frameHeight = 720, fireSeverity = "Medium",
            detections = new[] { new { category = "Fire", confidence = 0.9, bbox = new[] { 100.0, 100, 200, 200 } } } })).EnsureSuccessStatusCode();
        (await device.PostAsJsonAsync("/api/detections", new { cameraId = "cam-lobby", frameWidth = 1280, frameHeight = 720, detections = Array.Empty<object>() })).EnsureSuccessStatusCode();

        var watchdog = factory.Services.GetRequiredService<CameraWatchdog>();
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(10));                          // not silent long enough
        Assert.Empty(await CameraLost());

        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(31));
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(40));                          // still silent: no second alert
        var lost = await CameraLost();
        Assert.Equal(Severity.High, Assert.Single(lost, i => i.CameraId == "cam-server").Severity);   // fire open in Zone C
        Assert.Equal(Severity.Medium, Assert.Single(lost, i => i.CameraId == "cam-lobby").Severity);
        Assert.DoesNotContain(lost, i => i.CameraId == "drone-01");                         // never connected: no alarm
        Assert.Contains("Camera lost", lost[0].Message);

        // Back online, then silent again: the open camera-lost incident is updated, not duplicated.
        watchdog.FrameReceived("cam-server");
        await watchdog.CheckAsync(DateTime.UtcNow);
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(31));
        Assert.Single(await CameraLost(), i => i.CameraId == "cam-server");
    }

    private async Task<List<SmartSprinklerDto>> Sprinklers(HttpClient client) =>
        (await client.GetFromJsonAsync<List<SmartSprinklerDto>>("/api/iot/sprinklers", Json))!;

    private async Task<List<IncidentAlertDto>> CameraLost() =>
        (await (await factory.OperatorClientAsync()).GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents", Json))!
        .Where(i => i.Source == IncidentSource.CameraLost).ToList();
}
