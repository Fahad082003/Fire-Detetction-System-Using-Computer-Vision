using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

[Route("api/cameras")]
public class CamerasController(ICameraService cameras, IHttpClientFactory httpClients, ILogger<CamerasController> logger) : ApiControllerBase
{
    /// <summary>Only devices (the AI service) get the full stream link; dashboard users see credentials masked.</summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.OperatorOrDevice)]
    public async Task<IReadOnlyList<CameraFeedDto>> GetAll(CancellationToken ct) =>
        await cameras.GetAllAsync(ct, revealCredentials: User.IsInRole(AuthPolicies.DeviceRole));

    [HttpGet("{id}")]
    [Authorize(Policy = AuthPolicies.OperatorOrDevice)]
    public async Task<CameraFeedDto> Get(string id, CancellationToken ct) =>
        (await cameras.GetEntityAsync(id, ct)).ToDto(revealCredentials: User.IsInRole(AuthPolicies.DeviceRole));

    [HttpPost]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<ActionResult<CameraFeedDto>> Create(SaveCameraRequest request, CancellationToken ct)
    {
        var created = await cameras.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<CameraFeedDto> Update(string id, SaveCameraRequest request, CancellationToken ct) => await cameras.UpdateAsync(id, request, ct);

    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await cameras.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Links the camera to a floor plan with 4 point pairs, so the dashboard can place detected people on the plan.</summary>
    [HttpPut("{id}/calibration")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<CameraFeedDto> SaveCalibration(string id, CameraCalibrationDto calibration, CancellationToken ct) =>
        await cameras.SaveCalibrationAsync(id, calibration, ct);

    [HttpDelete("{id}/calibration")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task<CameraFeedDto> ClearCalibration(string id, CancellationToken ct) => await cameras.SaveCalibrationAsync(id, null, ct);

    /// <summary>
    /// Same-origin MJPEG relay for CCTV/drone feeds (browsers show it in &lt;img&gt;). Because &lt;img&gt; cannot send
    /// headers, the JWT may be passed as <c>?access_token=</c>.
    /// </summary>
    [HttpGet("{id}/stream")]
    [Authorize(Policy = AuthPolicies.Operator)]
    public async Task Stream(string id, CancellationToken ct)
    {
        var camera = await cameras.GetEntityAsync(id, ct);
        if (camera.StreamFormat != StreamFormat.Mjpeg ||
            !Uri.TryCreate(camera.StreamUrl, UriKind.Absolute, out var upstreamUri) ||
            upstreamUri.Scheme is not ("http" or "https"))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("Proxy supports http(s) MJPEG feeds only; play HLS/MP4 directly.", ct);
            return;
        }

        var client = httpClients.CreateClient("camera-stream");
        using var upstream = await client.GetAsync(upstreamUri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!upstream.IsSuccessStatusCode)
        {
            logger.LogWarning("MJPEG upstream for {Camera} returned {Status}", id, upstream.StatusCode);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace";
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await using var body = await upstream.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(Response.Body, ct); // runs until the browser closes the <img>
    }
}
