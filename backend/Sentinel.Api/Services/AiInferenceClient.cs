using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.DTOs;
using Sentinel.Api.Middleware;
using Sentinel.Api.Validators;

namespace Sentinel.Api.Services;

/// <summary>
/// Client for the external sentinel-ai inference service (folder ai-service/). It never fabricates results:
/// an unconfigured, offline, slow or malformed service becomes an explicit error, never empty "all clear" output.
/// Live camera frames do not pass through here; the service posts them to /api/detections itself.
/// </summary>
public class AiInferenceClient(HttpClient http, IOptions<AiServiceOptions> options)
{
    private readonly AiServiceOptions _opt = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<AiStatusDto> StatusAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.BaseUrl)) return new(false, false, "AiService:BaseUrl is not set.", null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3)); // a health check must stay fast even if inference is busy
        try
        {
            using var resp = await http.GetAsync(Url("health"), cts.Token);
            if (!resp.IsSuccessStatusCode) return new(true, false, $"Health check returned HTTP {(int)resp.StatusCode}.", null);
            return new(true, true, null, await resp.Content.ReadFromJsonAsync<JsonElement>(cts.Token));
        }
        catch (Exception e) when ((e is HttpRequestException or OperationCanceledException or JsonException) && !ct.IsCancellationRequested)
        {
            return new(true, false, e is JsonException ? "Health check returned invalid JSON." : $"AI service unreachable at {_opt.BaseUrl}.", null);
        }
    }

    /// <summary>Runs the real models on one operator-uploaded PNG/JPG. Preview only: no incident is opened.</summary>
    public async Task<AiAnalysisDto> AnalyzeAsync(IFormFile? file, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) throw new AppValidationException("file", "Choose a PNG or JPG image.");
        if (file.Length > _opt.MaxImageBytes)
            throw new AppValidationException("file", $"Image is larger than {_opt.MaxImageBytes / (1024 * 1024)} MB.");
        var bytes = new byte[file.Length];
        await using (var s = file.OpenReadStream()) await s.ReadExactlyAsync(bytes, ct);
        var ext = FloorPlanService.ImageExtension(bytes) ?? throw new AppValidationException("file", "Only PNG or JPG images are allowed.");
        if (string.IsNullOrWhiteSpace(_opt.BaseUrl))
            throw new DependencyException(503, "AI service not configured", "Set AiService:BaseUrl to the sentinel-ai service URL.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(ext == ".png" ? "image/png" : "image/jpeg");
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync(Url("infer?cameraId=upload-test"), content, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DependencyException(504, "AI service timeout", $"No inference result within {_opt.TimeoutSeconds} s.");
        }
        catch (HttpRequestException)
        {
            throw new DependencyException(503, "AI service unavailable", $"AI service unreachable at {_opt.BaseUrl}. Start ai-service (see ai-service/README.md).");
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.BadRequest) throw new AppValidationException("file", $"AI service rejected the image: {Short(body)}");
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) throw new DependencyException(503, "AI model not available", Short(body));
            if (!resp.IsSuccessStatusCode) throw new DependencyException(502, "AI service error", $"HTTP {(int)resp.StatusCode}: {Short(body)}");

            AiInferResponse? result;
            try { result = JsonSerializer.Deserialize<AiInferResponse>(body, Json); }
            catch (JsonException) { result = null; }
            if (result?.Payload is null) throw new DependencyException(502, "Malformed AI result", "AI service returned a result without a detection payload.");

            result.Payload.CameraId = "upload-test";
            try
            {
                return new AiAnalysisDto(DetectionPayloadValidator.Validate(result.Payload), Math.Round(result.InferenceMs, 1));
            }
            catch (AppValidationException e)
            {
                throw new DependencyException(502, "Malformed AI result",
                    "AI service result failed validation: " + string.Join("; ", e.Errors.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}")));
            }
        }
    }

    private Uri Url(string path) => new(new Uri(_opt.BaseUrl.TrimEnd('/') + "/"), path);

    private static string Short(string s) => s.Length > 300 ? s[..300] : s;
}
