using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace Sentinel.Api.Tests;

public class AiRateLimitFactory : FakeAiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:AiAnalyze:PermitLimit", "3");
        builder.UseSetting("RateLimits:AiAnalyze:WindowSeconds", "2");
    }
}

public class AiRateLimitTests(AiRateLimitFactory factory) : IClassFixture<AiRateLimitFactory>
{
    private static MultipartFormDataContent Png()
    {
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "test.png" } };
    }

    [Fact]
    public async Task Analyze_is_limited_per_user_recovers_after_the_window_and_never_blocks_live_frames()
    {
        var op = await factory.OperatorClientAsync();
        for (var i = 0; i < 3; i++)  // within the limit: reaches the (stubbed) AI service
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await op.PostAsync("/api/ai/analyze", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await op.PostAsync("/api/ai/analyze", Png())).StatusCode);

        // Live camera frames use another endpoint and are not limited.
        var frame = await factory.DeviceClient().PostAsJsonAsync("/api/detections", new
        {
            cameraId = "cam-lobby", frameWidth = 640, frameHeight = 480,
            detections = new[] { new { category = "Human", confidence = 0.9, bbox = new[] { 100.0, 100, 50, 150 } } },
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, frame.StatusCode);

        // Still authenticated first: no token is 401, not 429.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/api/ai/analyze", Png())).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(2.5));  // window over
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await op.PostAsync("/api/ai/analyze", Png())).StatusCode);
    }
}
