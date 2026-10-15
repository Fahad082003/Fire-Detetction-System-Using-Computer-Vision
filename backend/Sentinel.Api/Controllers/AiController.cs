using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Security;
using Sentinel.Api.Services;

namespace Sentinel.Api.Controllers;

/// <summary>AI inference service status and operator test-image analysis (preview only, never opens incidents).</summary>
[Route("api/ai")]
[Authorize(Policy = AuthPolicies.Operator)]
public class AiController(AiInferenceClient ai) : ApiControllerBase
{
    [HttpGet("status")]
    public async Task<AiStatusDto> Status(CancellationToken ct) => await ai.StatusAsync(ct);

    /// <summary>Multipart field "file": PNG/JPG, max 5 MB. 503 = service/model unavailable, 504 = timeout, 502 = malformed result.</summary>
    [HttpPost("analyze")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<AiAnalysisDto> Analyze(IFormFile? file, CancellationToken ct) => await ai.AnalyzeAsync(file, ct);
}
