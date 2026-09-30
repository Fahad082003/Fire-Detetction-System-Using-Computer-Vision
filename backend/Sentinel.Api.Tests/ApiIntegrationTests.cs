using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Api.DTOs;
using Sentinel.Api.Models;

namespace Sentinel.Api.Tests;

public class ApiIntegrationTests(SentinelApiFactory factory) : IClassFixture<SentinelApiFactory>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = TestJson.Options;

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = SentinelApiFactory.AdminEmail, password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Snapshot_requires_login_and_returns_seeded_devices()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/dashboard/snapshot")).StatusCode);

        var client = await factory.OperatorClientAsync();
        var snapshot = await client.GetFromJsonAsync<DashboardSnapshotDto>("/api/dashboard/snapshot", Json);
        Assert.Equal(4, snapshot!.Cameras.Count);
        Assert.Equal(6, snapshot.Sensors.Count);
        Assert.Equal(5, snapshot.Sprinklers.Count);
    }

    [Fact]
    public async Task Wrong_device_key_is_rejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/iot/sensors")).StatusCode);
    }

    [Fact]
    public async Task Critical_temperature_opens_incident_and_auto_sprinklers()
    {
        var device = factory.DeviceClient();
        var resp = await device.PostAsJsonAsync("/api/iot/sensors/temp-a1/readings", new { value = 80.0 });
        resp.EnsureSuccessStatusCode();
        var sensor = await resp.Content.ReadFromJsonAsync<SensorNodeDto>(Json);
        Assert.Equal(SensorState.Critical, sensor!.State);

        var op = await factory.OperatorClientAsync();
        var incidents = await op.GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents?resolved=false", Json);
        Assert.Contains(incidents!, i => i.SensorId == "temp-a1" && i.Severity == Severity.Critical);

        var sprinklers = await device.GetFromJsonAsync<List<SmartSprinklerDto>>("/api/iot/sprinklers", Json);
        Assert.All(sprinklers!.Where(s => s.Zone == "Zone A"), s => Assert.True(s.IsActive));
    }

    [Fact]
    public async Task Detection_with_humans_in_danger_creates_critical_incident()
    {
        var device = factory.DeviceClient();
        var resp = await device.PostAsJsonAsync("/api/detections", new
        {
            cameraId = "cam-server", frameWidth = 1280, frameHeight = 720,
            fireSeverity = "Medium",
            detections = new object[]
            {
                new { category = "Fire", confidence = 0.9, bbox = new[] { 100.0, 100, 200, 200 } },
                new { category = "Human", confidence = 0.95, bbox = new[] { 150.0, 150, 40, 120 }, atRisk = true },
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var op = await factory.OperatorClientAsync();
        var incidents = await op.GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents", Json);
        var incident = Assert.Single(incidents!, i => i.CameraId == "cam-server");
        Assert.Equal(Severity.Critical, incident.Severity);
        Assert.Equal((1, 1), (incident.HumanCount, incident.DangerCount));
        var sprinkler = (await device.GetFromJsonAsync<List<SmartSprinklerDto>>("/api/iot/sprinklers", Json))!.Single(s => s.Id == "spr-c1");
        Assert.True(sprinkler.IsActive);
    }

    [Fact]
    public async Task Invalid_detection_returns_validation_problem()
    {
        var resp = await factory.DeviceClient().PostAsJsonAsync("/api/detections", new { cameraId = "cam-lobby", detections = new[] { new { bbox = new[] { 1.0, 2 } } } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("detections[0].bbox", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sprinkler_toggle_and_mode_change()
    {
        var device = factory.DeviceClient();
        var on = await (await device.PostAsJsonAsync("/api/iot/sprinklers/spr-b2/toggle", new { isActive = true })).Content.ReadFromJsonAsync<SmartSprinklerDto>(Json);
        Assert.True(on!.IsActive);
        Assert.NotNull(on.LastTriggeredAt);
        var manual = await (await device.PostAsJsonAsync("/api/iot/sprinklers/spr-b2/toggle", new { mode = "Manual" })).Content.ReadFromJsonAsync<SmartSprinklerDto>(Json);
        Assert.Equal(SprinklerMode.Manual, manual!.Mode);
        Assert.Equal(HttpStatusCode.NotFound, (await device.PostAsJsonAsync("/api/iot/sprinklers/nope/toggle", new { })).StatusCode);
    }

    [Fact]
    public async Task Manual_dispatch_requires_operator_and_can_be_resolved()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.DeviceClient().PostAsJsonAsync("/api/incidents/dispatch", new { zone = "Zone A" })).StatusCode);

        var op = await factory.OperatorClientAsync();
        var resp = await op.PostAsJsonAsync("/api/incidents/dispatch", new { zone = "Zone A", message = "Evacuate via east exit" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var incident = await resp.Content.ReadFromJsonAsync<IncidentAlertDto>(Json);
        Assert.Equal(IncidentSource.Manual, incident!.Source);

        var resolved = await (await op.PostAsync($"/api/incidents/{incident.Id}/resolve", null)).Content.ReadFromJsonAsync<IncidentAlertDto>(Json);
        Assert.True(resolved!.IsResolved);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/incidents/dispatch", new { zone = "" })).StatusCode);
    }

    [Fact]
    public async Task Hub_broadcasts_device_detection_to_dashboard()
    {
        var token = await factory.LoginAsync();
        await using var viewer = Connect(o => o.AccessTokenProvider = () => Task.FromResult<string?>(token));
        await using var device = Connect(o => o.Headers.Add("X-Device-Key", SentinelApiFactory.DeviceKey));

        var snapshot = new TaskCompletionSource<bool>();
        var received = new TaskCompletionSource<DetectionFrameDto>();
        viewer.On<DashboardSnapshotDto>("snapshot", _ => snapshot.TrySetResult(true));
        viewer.On<DetectionFrameDto>("detection", f => received.TrySetResult(f));
        await viewer.StartAsync();
        await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await device.StartAsync();

        var ack = await device.InvokeAsync<Sentinel.Api.Hubs.HubResult>("PublishDetection", new DetectionPayloadDto
        {
            CameraId = "cam-lobby", Normalized = true,
            Detections = [new DetectionDto { Category = DetectionCategory.Human, Bbox = [0.1, 0.1, 0.2, 0.4], TrackId = "p1" }],
        });
        Assert.True(ack.Ok);

        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cam-lobby", frame.CameraId);
        Assert.Equal(1, frame.HumanCount);
    }

    [Fact]
    public async Task Viewer_cannot_publish_detections()
    {
        var token = await factory.LoginAsync();
        await using var viewer = Connect(o => o.AccessTokenProvider = () => Task.FromResult<string?>(token));
        await viewer.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => viewer.InvokeAsync<object>("PublishDetection", new DetectionPayloadDto { CameraId = "cam-lobby" }));
    }

    private HubConnection Connect(Action<HttpConnectionOptions> configure) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/monitoring"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                configure(o);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
}
