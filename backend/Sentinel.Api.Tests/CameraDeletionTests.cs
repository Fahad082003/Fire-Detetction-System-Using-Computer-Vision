using System.Net;
using System.Net.Http.Json;
using Sentinel.Api.DTOs;

namespace Sentinel.Api.Tests;

/// <summary>Own fixture (fresh database): asserts the exact set of incidents.</summary>
public class CameraDeletionTests(SentinelApiFactory factory) : IClassFixture<SentinelApiFactory>
{
    private static object Fire(string camera) => new
    {
        cameraId = camera, frameWidth = 1280, frameHeight = 720, fireSeverity = "Medium",
        detections = new[] { new { category = "Fire", confidence = 0.9, bbox = new[] { 100.0, 100, 200, 200 } } },
    };

    [Fact]
    public async Task Late_frames_of_a_deleted_camera_are_refused_but_unknown_cameras_still_alert()
    {
        var admin = await factory.OperatorClientAsync();
        var device = factory.DeviceClient();
        var camera = new { id = "cam-gone", name = "Gone", feedType = "CCTV", streamFormat = "Simulated", locationZone = "Ground Floor - Store" };
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/cameras", camera)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/cameras/cam-gone")).StatusCode);

        // Deleted while still streaming: the late fire frame is refused (404 makes the AI service stop the camera).
        Assert.Equal(HttpStatusCode.NotFound, (await device.PostAsJsonAsync("/api/detections", Fire("cam-gone"), TestJson.Options)).StatusCode);

        // Fail-safe kept: a camera that was never registered still raises its fire, under "Unassigned".
        Assert.Equal(HttpStatusCode.Accepted, (await device.PostAsJsonAsync("/api/detections", Fire("cam-never-added"), TestJson.Options)).StatusCode);

        var incidents = (await admin.GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents?resolved=false", TestJson.Options))!;
        var unassigned = Assert.Single(incidents, i => i.Zone == "Unassigned");
        Assert.Contains("cam-never-added", unassigned.Message);
        Assert.DoesNotContain(incidents, i => i.Message.Contains("cam-gone"));

        // Added again under the same id: its frames count again.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/cameras", camera)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await device.PostAsJsonAsync("/api/detections", Fire("cam-gone"), TestJson.Options)).StatusCode);
    }
}
