using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Models;
using Sentinel.Api.Services;

namespace Sentinel.Api.Tests;

/// <summary>API with the sentinel-ai HTTP calls answered by a stub. MOCK-BASED: proves the integration contract, not model accuracy.</summary>
public class FakeAiFactory : SentinelApiFactory
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    public int Calls;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("AiService:BaseUrl", "http://ai.test");
        builder.UseSetting("AiService:TimeoutSeconds", "1");
        builder.ConfigureServices(s => s.AddHttpClient<AiInferenceClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new Stub((r, ct) => { Interlocked.Increment(ref Calls); return Respond(r, ct); })));
    }

    private class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => f(r, ct);
    }
}

public class AiServiceTests(FakeAiFactory factory) : IClassFixture<FakeAiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static MultipartFormDataContent File(byte[] bytes, string name = "test.png")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "file", name);
        return form;
    }

    private const string InferResult = """
        {"inferenceMs": 412.7, "payload": {"cameraId": "x", "frameWidth": 640, "frameHeight": 480, "model": "person:yolox_tiny fire:fire_smoke",
          "detections": [
            {"label": "person", "category": "Human", "confidence": 0.88, "bbox": [10, 20, 50, 120], "trackId": "P1", "explanation": "person 0.88 >= 0.45"},
            {"label": "fire", "category": "Fire", "confidence": 0.71, "bbox": [300, 200, 80, 60], "explanation": "fire 0.71 >= 0.40"},
            {"label": "smoke", "category": "Smoke", "confidence": 0.52, "bbox": [280, 100, 150, 90]}],
          "fireSeverity": "Medium"}}
        """;

    [Fact]
    public async Task Status_reports_online_service_and_its_health_json()
    {
        factory.Respond = (r, _) => Task.FromResult(Json("""{"status":"ok","models":[{"name":"person","loaded":true}]}"""));
        var s = await (await factory.OperatorClientAsync()).GetFromJsonAsync<AiStatusDto>("/api/ai/status", TestJson.Options);
        Assert.True(s!.Configured);
        Assert.True(s.Online);
        Assert.Equal("ok", s.Service!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Status_reports_offline_and_malformed_health_without_failing()
    {
        var client = await factory.OperatorClientAsync();
        factory.Respond = (_, _) => throw new HttpRequestException("connection refused");
        var down = await client.GetFromJsonAsync<AiStatusDto>("/api/ai/status", TestJson.Options);
        Assert.False(down!.Online);
        Assert.Contains("unreachable", down.Error);

        factory.Respond = (_, _) => Task.FromResult(Json("<html>"));
        var bad = await client.GetFromJsonAsync<AiStatusDto>("/api/ai/status", TestJson.Options);
        Assert.False(bad!.Online);
        Assert.Contains("invalid JSON", bad.Error);
    }

    [Fact]
    public async Task Analyze_maps_model_output_and_never_opens_an_incident()
    {
        factory.Respond = (_, _) => Task.FromResult(Json(InferResult));
        var before = await IncidentCount();
        var resp = await (await factory.OperatorClientAsync()).PostAsync("/api/ai/analyze", File(Png));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var a = (await resp.Content.ReadFromJsonAsync<AiAnalysisDto>(TestJson.Options))!;

        Assert.Equal("upload-test", a.Frame.CameraId);
        Assert.Equal(412.7, a.InferenceMs);
        Assert.Equal("person:yolox_tiny fire:fire_smoke", a.Frame.Model);
        Assert.True(a.Frame.FireDetected);
        Assert.True(a.Frame.SmokeDetected);
        Assert.Equal(Severity.Medium, a.Frame.FireSeverity);
        Assert.Equal(1, a.Frame.HumanCount);
        Assert.Equal("P1", a.Frame.Detections[0].TrackId);
        Assert.Equal("person 0.88 >= 0.45", a.Frame.Detections[0].Explanation);
        Assert.Equal(before, await IncidentCount());
    }

    [Fact]
    public async Task Analyze_rejects_fake_image_before_calling_the_service()
    {
        var calls = factory.Calls;
        var resp = await (await factory.OperatorClientAsync()).PostAsync("/api/ai/analyze", File("not an image"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(calls, factory.Calls);
    }

    [Theory]
    [InlineData("down", HttpStatusCode.ServiceUnavailable)]
    [InlineData("model-missing", HttpStatusCode.ServiceUnavailable)]
    [InlineData("timeout", HttpStatusCode.GatewayTimeout)]
    [InlineData("not-json", HttpStatusCode.BadGateway)]
    [InlineData("no-payload", HttpStatusCode.BadGateway)]
    [InlineData("invalid-confidence", HttpStatusCode.BadGateway)]
    [InlineData("server-error", HttpStatusCode.BadGateway)]
    [InlineData("undecodable", HttpStatusCode.BadRequest)]
    public async Task Analyze_turns_service_failures_into_explicit_errors(string failure, HttpStatusCode expected)
    {
        factory.Respond = failure switch
        {
            "down" => (_, _) => throw new HttpRequestException("connection refused"),
            "model-missing" => (_, _) => Task.FromResult(Json("""{"error":"fire model not loaded"}""", HttpStatusCode.ServiceUnavailable)),
            "timeout" => async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return Json(InferResult); },
            "not-json" => (_, _) => Task.FromResult(Json("<html>oops</html>")),
            "no-payload" => (_, _) => Task.FromResult(Json("""{"inferenceMs": 3}""")),
            "invalid-confidence" => (_, _) => Task.FromResult(Json("""{"payload":{"detections":[{"category":"Fire","confidence":7,"bbox":[0,0,1,1]}]}}""")),
            "server-error" => (_, _) => Task.FromResult(Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)),
            _ => (_, _) => Task.FromResult(Json("""{"error":"cannot decode image"}""", HttpStatusCode.BadRequest)),
        };
        var resp = await (await factory.OperatorClientAsync()).PostAsync("/api/ai/analyze", File(Png));
        Assert.Equal(expected, resp.StatusCode);
        Assert.Contains("problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Ai_endpoints_require_an_operator()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/ai/status")).StatusCode);
        // The Operator policy only accepts operator JWTs, so a device key is not an identity here.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.DeviceClient().GetAsync("/api/ai/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/api/ai/analyze", File(Png))).StatusCode);
    }

    [Fact]
    public async Task Smoke_only_frame_opens_a_medium_non_emergency_incident_tagged_with_the_model()
    {
        var resp = await factory.DeviceClient().PostAsJsonAsync("/api/detections", new
        {
            cameraId = "cam-server", frameWidth = 640, frameHeight = 480, model = "fire:fire_smoke",
            detections = new[] { new { category = "Smoke", confidence = 0.6, bbox = new[] { 10.0, 10, 200, 100 }, explanation = "smoke 0.60 >= 0.40, seen 3/4 frames" } },
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var incident = await db.IncidentAlerts.Where(i => i.Zone == "First Floor - Server Hall" && !i.IsResolved).OrderByDescending(i => i.Id).FirstAsync();
        Assert.Equal(Severity.Medium, incident.Severity);
        Assert.Contains("Smoke detected", incident.Message);
        Assert.Contains("[AI fire:fire_smoke]", incident.Message);
        Assert.False(await db.SmartSprinklers.AnyAsync(s => s.Zone == "First Floor - Server Hall" && s.IsActive)); // not an emergency
    }

    [Theory]
    [InlineData("cam-lobby", "Ground Floor - Lobby", true, false)]  // simulated test input: incident yes, relays untouched
    [InlineData("drone-01", "Ground Floor - Warehouse", false, true)]   // live data: existing emergency rule switches Auto relays
    public async Task Simulated_emergency_frames_never_switch_sprinklers(string camera, string zone, bool simulated, bool expectActive)
    {
        var resp = await factory.DeviceClient().PostAsJsonAsync("/api/detections", new
        {
            cameraId = camera, frameWidth = 640, frameHeight = 480, simulated,
            detections = new[] { new { category = "Human", confidence = 0.9, bbox = new[] { 100.0, 100, 50, 150 }, trackId = "P1" } },
            dangerZones = new[] { new { label = "Fuel store", level = "Danger", polygon = new[] { new[] { 0.0, 200 }, new[] { 300.0, 200 }, new[] { 300.0, 300 }, new[] { 0.0, 300 } } } },
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        Assert.True(await db.IncidentAlerts.AnyAsync(i => i.Zone == zone && !i.IsResolved && i.DangerCount > 0));
        Assert.Equal(expectActive, await db.SmartSprinklers.AnyAsync(s => s.Zone == zone && s.IsActive));
    }

    [Fact]
    public async Task Overlong_explanation_is_rejected()
    {
        var resp = await factory.DeviceClient().PostAsJsonAsync("/api/detections", new
        {
            cameraId = "cam-lobby",
            detections = new[] { new { category = "Human", bbox = new[] { 1.0, 2, 3, 4 }, explanation = new string('x', 241) } },
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private async Task<int> IncidentCount()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SentinelDbContext>().IncidentAlerts.CountAsync();
    }
}
