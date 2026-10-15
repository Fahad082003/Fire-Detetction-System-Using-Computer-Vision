using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Sentinel.Api.DTOs;
using Sentinel.Api.Models;

namespace Sentinel.Api.Tests;

public class FloorPlanAndLogTests(SentinelApiFactory factory) : IClassFixture<SentinelApiFactory>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = TestJson.Options;
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static object Plan(double dangerX = 17) => new
    {
        name = "Ground floor", building = "Warehouse", floorNumber = 0, widthMeters = 20, heightMeters = 10,
        elements = new object[]
        {
            new { type = "Corridor", name = "Main corridor", x = 0, y = 4, width = 20, height = 2 },
            new { type = "Exit", name = "East", x = 19, y = 4, width = 1, height = 2 },
            new { type = "Exit", name = "West", x = 0, y = 4, width = 1, height = 2 },
            new { type = "SafeZone", name = "Assembly point", x = 0, y = 7, width = 3, height = 3 },
            // Not marked active: it becomes active only while Zone C has an open incident.
            new { type = "DangerZone", name = "Boiler room", x = dangerX, y = 4, width = 1, height = 2, zone = "Zone C" },
        },
    };

    [Fact]
    public async Task Floor_plan_crud_image_route_and_route_log()
    {
        var op = await factory.OperatorClientAsync();

        var created = await op.PostAsJsonAsync("/api/floorplans", Plan());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var plan = (await created.Content.ReadFromJsonAsync<FloorPlanDto>(Json))!;
        Assert.Equal(5, plan.Elements.Count);
        Assert.Contains(plan.Elements, e => e.Type == FloorElementType.SafeZone && e.Name == "Assembly point");

        // Geometry validation: element outside the 20 x 10 m plan is rejected.
        var bad = await op.PostAsJsonAsync("/api/floorplans", Plan(dangerX: 25));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Image upload: PNG accepted, a text file renamed .png rejected (magic bytes checked).
        Assert.Equal(HttpStatusCode.OK, (await Upload(op, plan.Id, Png, "plan.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(op, plan.Id, "not an image"u8.ToArray(), "fake.png")).StatusCode);
        var image = await op.GetAsync($"/api/floorplans/{plan.Id}/image");
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);

        // Route with no incident: nearest exit East; ETA = distance / 1.2 m/s.
        var r1 = await Route(op, plan.Id, 16, 5);
        Assert.True(r1.Found);
        Assert.Equal("East", r1.ExitName);
        Assert.Equal(Math.Round(r1.DistanceMeters / 1.2, 1), r1.EstimatedSeconds);
        Assert.Empty(r1.ActiveDangerZones);

        // An open incident in Zone C activates the linked danger zone → route switches to West.
        (await op.PostAsJsonAsync("/api/incidents/dispatch", new { zone = "Zone C", message = "test" })).EnsureSuccessStatusCode();
        var r2 = await Route(op, plan.Id, 16, 5);
        Assert.True(r2.Found);
        Assert.Equal("West", r2.ExitName);
        Assert.Contains("Boiler room", r2.ActiveDangerZones);

        // Start outside the plan is a 400; both estimates were logged once each.
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/floorplans/{plan.Id}/route", new { x = 50, y = 5 })).StatusCode);
        await Route(op, plan.Id, 16, 5); // identical repeat → not logged again
        var logs = (await op.GetFromJsonAsync<List<IncidentLogDto>>("/api/incidents/logs?type=EvacuationRoute", Json))!
            .Where(l => l.Zone.StartsWith($"Floor plan #{plan.Id}")).ToList();
        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, l => l.Details.StartsWith("ESTIMATE") && l.Details.Contains("exit 'East'"));
        Assert.Contains(logs, l => l.Details.StartsWith("ESTIMATE") && l.Details.Contains("exit 'West'"));

        Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync($"/api/floorplans/{plan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/floorplans/{plan.Id}")).StatusCode);
    }

    [Fact]
    public async Task Person_in_danger_zone_is_detected_by_backend_and_logged_once()
    {
        var device = factory.DeviceClient();
        object Frame(bool fire) => new
        {
            cameraId = "cam-lobby", frameWidth = 1280, frameHeight = 720, simulated = true,
            fireSeverity = fire ? "Critical" : null,
            detections = fire
                ? new object[]
                {
                    new { category = "Human", bbox = new[] { 300.0, 200, 40, 120 }, trackId = "p9" },
                    new { category = "Fire", bbox = new[] { 400.0, 200, 100, 100 } },
                }
                : [new { category = "Human", bbox = new[] { 300.0, 200, 40, 120 }, trackId = "p9" }],
            // Person p9's feet are at (320, 320): inside this zone. No atRisk / dangerCount sent by the producer.
            dangerZones = new[] { new { label = "Lobby fire area", level = "Danger", polygon = new[] { new[] { 250.0, 150 }, new[] { 600.0, 150 }, new[] { 600.0, 400 }, new[] { 250.0, 400 } } } },
        };

        var first = await (await device.PostAsJsonAsync("/api/detections", Frame(fire: false))).Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("1", first!["dangerCount"].ToString());
        await device.PostAsJsonAsync("/api/detections", Frame(fire: true)); // escalates High → Critical, same person + zone

        var op = await factory.OperatorClientAsync();
        var incident = (await op.GetFromJsonAsync<List<IncidentAlertDto>>("/api/incidents", Json))!.Single(i => i.CameraId == "cam-lobby");
        Assert.Equal(Severity.Critical, incident.Severity);
        Assert.Contains("Lobby fire area", incident.Message);
        Assert.Contains("[SIMULATED DATA]", incident.Message);

        var logs = (await op.GetFromJsonAsync<List<IncidentLogDto>>($"/api/incidents/logs?incidentId={incident.Id}", Json))!;
        var person = Assert.Single(logs, l => l.Type == IncidentLogType.PersonInDanger); // not duplicated by frame 2
        Assert.Equal("p9", person.PersonTrackId);
        Assert.Equal("Lobby fire area", person.DangerZone);
        Assert.Equal((320.0, 320.0), (person.PersonX!.Value, person.PersonY!.Value));
        Assert.True(person.Simulated);
        Assert.Contains(logs, l => l.Type == IncidentLogType.Opened);
        Assert.Contains(logs, l => l.Type == IncidentLogType.Escalated);
        var measurement = Assert.Single(logs, l => l.Type == IncidentLogType.FireMeasurement);
        Assert.Equal(1.09, measurement.FireAreaPercent); // 100*100 / (1280*720) = 1.085 %

        var filtered = await op.GetFromJsonAsync<List<IncidentLogDto>>($"/api/incidents/logs?incidentId={incident.Id}&type=FireMeasurement", Json);
        Assert.All(filtered!, l => Assert.Equal(IncidentLogType.FireMeasurement, l.Type));
    }

    [Fact]
    public async Task Camera_calibration_is_validated_saved_and_cleared()
    {
        var op = await factory.OperatorClientAsync();
        var plan = (await (await op.PostAsJsonAsync("/api/floorplans", Plan())).Content.ReadFromJsonAsync<FloorPlanDto>(Json))!;
        const string url = "/api/cameras/cam-lobby/calibration";
        double[][] image = [[100, 600], [1100, 600], [900, 200], [300, 200]], floor = [[0, 0], [20, 0], [20, 10], [0, 10]];
        object Body(double[][] planPoints, int? planId = null) => new { floorPlanId = planId ?? plan.Id, imagePoints = image, planPoints };

        Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, Body([[0, 0], [10, 0], [20, 0], [0, 10]]))).StatusCode); // 3 on one line
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, Body([[0, 0], [25, 0], [20, 10], [0, 10]]))).StatusCode); // outside 20 x 10 m
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, Body(floor, planId: 999_999))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.DeviceClient().PutAsJsonAsync(url, Body(floor))).StatusCode);

        var saved = await (await op.PutAsJsonAsync(url, Body(floor))).Content.ReadFromJsonAsync<CameraFeedDto>(Json);
        Assert.Equal(plan.Id, saved!.Calibration!.FloorPlanId);
        var listed = await op.GetFromJsonAsync<List<CameraFeedDto>>("/api/cameras", Json);
        Assert.Equal(floor, listed!.Single(c => c.Id == "cam-lobby").Calibration!.PlanPoints);

        var cleared = await (await op.DeleteAsync(url)).Content.ReadFromJsonAsync<CameraFeedDto>(Json);
        Assert.Null(cleared!.Calibration);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, int id, byte[] bytes, string name)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(file, "file", name);
        return await client.PutAsync($"/api/floorplans/{id}/image", content);
    }

    private static async Task<RouteResultDto> Route(HttpClient client, int id, double x, double y)
    {
        var resp = await client.PostAsJsonAsync($"/api/floorplans/{id}/route", new { x, y });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RouteResultDto>(Json))!;
    }
}
