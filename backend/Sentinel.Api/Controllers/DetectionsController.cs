using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

/// <summary>REST ingestion of AI inference frames (alternative to the SignalR PublishDetection method).</summary>
[Route("api/detections")]
[Authorize(Policy = AuthPolicies.OperatorOrDevice)]
public class DetectionsController(IDetectionService detections) : ApiControllerBase
{
    public record DetectionAccepted(bool Accepted, string CameraId, bool FireDetected, int HumanCount, int DangerCount, int? IncidentId);

    [HttpPost]
    [ProducesResponseType<DetectionAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Post([FromBody] DetectionPayloadDto payload, CancellationToken ct)
    {
        var (frame, incidentId) = await detections.ProcessAsync(payload, ct);
        return Accepted(new DetectionAccepted(true, frame.CameraId, frame.FireDetected, frame.HumanCount, frame.DangerCount, incidentId));
    }
}
