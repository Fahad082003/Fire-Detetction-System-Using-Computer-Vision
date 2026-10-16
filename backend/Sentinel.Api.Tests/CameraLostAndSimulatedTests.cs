using System.Net;
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
        Assert.DoesNotContain(await Sprinklers(device), s => s.Zone == "Ground Floor - Warehouse" && s.IsActive);

        // Control: the same reading without the flag is real data and opens the zone's Auto relays.
        (await device.PostAsJsonAsync("/api/iot/telemetry", new { readings = new[] { new { sensorId = "temp-b1", value = 81.0 } } })).EnsureSuccessStatusCode();
        Assert.Contains(await Sprinklers(device), s => s.Zone == "Ground Floor - Warehouse" && s.IsActive);
    }

    [Fact]
    public async Task Extreme_smoke_reading_raises_a_critical_alert_instead_of_being_rejected()
    {
        // Bug: values above 100 000 ppm were dropped, so the worst smoke produced no alert at all.
        var device = factory.DeviceClient();
        (await device.PostAsJsonAsync("/api/iot/telemetry", new { readings = new[] { new { sensorId = "smoke-a1", value = 250_000.0 } } })).EnsureSuccessStatusCode();
        var op = await factory.OperatorClientAsync();
        var incident = (await op.GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents", Json))!.Single(i => i.SensorId == "smoke-a1");
        Assert.Equal(Severity.Critical, incident.Severity);

        // Beyond the physical maximum it is still rejected, now with a reason the operator can read.
        var bad = await device.PostAsJsonAsync("/api/iot/telemetry", new { readings = new[] { new { sensorId = "smoke-a1", value = 2_000_000.0 } } });
        Assert.Contains("must be between", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Silent_camera_raises_one_camera_lost_alert_high_while_its_zone_burns()
    {
        var device = factory.DeviceClient();
        // cam-server (First Floor - Server Hall) reports a fire; cam-lobby (Ground Floor - Lobby) sends a quiet frame. drone-01 never sends anything.
        (await device.PostAsJsonAsync("/api/detections", new { cameraId = "cam-server", frameWidth = 1280, frameHeight = 720, fireSeverity = "Medium",
            detections = new[] { new { category = "Fire", confidence = 0.9, bbox = new[] { 100.0, 100, 200, 200 } } } })).EnsureSuccessStatusCode();
        (await device.PostAsJsonAsync("/api/detections", new { cameraId = "cam-lobby", frameWidth = 1280, frameHeight = 720, detections = Array.Empty<object>() })).EnsureSuccessStatusCode();

        var watchdog = factory.Services.GetRequiredService<CameraWatchdog>();
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(5));                           // not silent long enough
        Assert.Empty(await CameraLost());

        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(31));
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(40));                          // still silent: no second alert
        var lost = await CameraLost();
        Assert.Equal(Severity.High, Assert.Single(lost, i => i.CameraId == "cam-server").Severity);   // fire open in First Floor - Server Hall
        Assert.Equal(Severity.Medium, Assert.Single(lost, i => i.CameraId == "cam-lobby").Severity);
        Assert.DoesNotContain(lost, i => i.CameraId == "drone-01");                         // never connected: no alarm
        Assert.Contains("Camera lost", lost[0].Message);

        // Back online, then silent again: the open camera-lost incident is updated, not duplicated.
        watchdog.FrameReceived("cam-server");
        await watchdog.CheckAsync(DateTime.UtcNow);
        await watchdog.CheckAsync(DateTime.UtcNow.AddSeconds(31));
        Assert.Single(await CameraLost(), i => i.CameraId == "cam-server");
    }

    [Fact]
    public async Task Admin_manages_cameras_and_stream_credentials_reach_only_devices()
    {
        var admin = await factory.OperatorClientAsync(); // the seeded user is an Admin
        var device = factory.DeviceClient();
        var body = new { id = "cam-gate", name = "Main Gate", feedType = "CCTV", streamUrl = "rtsp://admin:s3cret@10.0.0.20/live", streamFormat = "Simulated", locationZone = "Ground Floor - Gate" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsJsonAsync("/api/cameras", body)).StatusCode);   // devices cannot add cameras
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/cameras", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/cameras", body)).StatusCode);        // same id twice
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/cameras", new { id = "bad id!", name = "x", locationZone = "A" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/cameras", new { id = "cam-x", name = "x", locationZone = "Block A" })).StatusCode); // no floor
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/cameras", new { id = "cam-x", name = "x", locationZone = "A", streamUrl = "ftp://x" })).StatusCode);

        // Dashboard users see the password masked; the AI service (device key) gets the real link.
        var seen = (await admin.GetFromJsonAsync<List<CameraFeedDto>>("/api/cameras", Json))!.Single(c => c.Id == "cam-gate");
        Assert.Equal("rtsp://***@10.0.0.20/live", seen.StreamUrl);
        Assert.Equal("rtsp://admin:s3cret@10.0.0.20/live", (await device.GetFromJsonAsync<List<CameraFeedDto>>("/api/cameras", Json))!.Single(c => c.Id == "cam-gate").StreamUrl);

        // Renaming with the masked link sent back keeps the saved credentials.
        var renamed = await (await admin.PutAsJsonAsync("/api/cameras/cam-gate", new { name = "Front Gate", feedType = "CCTV", streamUrl = seen.StreamUrl, streamFormat = "Simulated", locationZone = "Ground Floor - Gate" })).Content.ReadFromJsonAsync<CameraFeedDto>(Json);
        Assert.Equal("Front Gate", renamed!.Name);
        Assert.Equal("rtsp://admin:s3cret@10.0.0.20/live", (await device.GetFromJsonAsync<CameraFeedDto>("/api/cameras/cam-gate", Json))!.StreamUrl);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/cameras/cam-gate")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/cameras/cam-gate")).StatusCode);
    }

    private async Task<List<SmartSprinklerDto>> Sprinklers(HttpClient client) =>
        (await client.GetFromJsonAsync<List<SmartSprinklerDto>>("/api/iot/sprinklers", Json))!;

    private async Task<List<IncidentAlertDto>> CameraLost() =>
        (await (await factory.OperatorClientAsync()).GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents", Json))!
        .Where(i => i.Source == IncidentSource.CameraLost).ToList();
}
