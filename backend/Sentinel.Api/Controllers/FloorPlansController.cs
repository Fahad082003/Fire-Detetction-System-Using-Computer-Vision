using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Security;

namespace Sentinel.Api.Controllers;

/// <summary>Building floor plans: CRUD, reference image, and evacuation route estimates.</summary>
[Route("api/floorplans")]
[Authorize(Policy = AuthPolicies.Operator)]
public class FloorPlansController(IFloorPlanService plans) : ApiControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<FloorPlanDto>> List(CancellationToken ct) => await plans.ListAsync(ct);

    [HttpGet("{id:int}")]
    public async Task<FloorPlanDto> Get(int id, CancellationToken ct) => await plans.GetAsync(id, ct);

    [HttpPost]
    [ProducesResponseType<FloorPlanDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveFloorPlanRequest request, CancellationToken ct)
    {
        var plan = await plans.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = plan.Id }, plan);
    }

    /// <summary>Replaces name, size and all elements.</summary>
    [HttpPut("{id:int}")]
    public async Task<FloorPlanDto> Update(int id, SaveFloorPlanRequest request, CancellationToken ct) =>
        await plans.UpdateAsync(id, request, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await plans.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Upload a PNG/JPG reference image (multipart field "file", max 5 MB). It is only a background:
    /// walls, exits and routes are NOT detected from the image.
    /// </summary>
    [HttpPut("{id:int}/image")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<FloorPlanDto> UploadImage(int id, IFormFile file, CancellationToken ct) =>
        await plans.SaveImageAsync(id, file, ct);

    [HttpGet("{id:int}/image")]
    public async Task<IActionResult> Image(int id, CancellationToken ct)
    {
        var (path, contentType) = await plans.GetImageAsync(id, ct);
        return PhysicalFile(path, contentType);
    }

    /// <summary>Estimated evacuation route from a known start point (meters) to the nearest reachable exit.</summary>
    [HttpPost("{id:int}/route")]
    public async Task<RouteResultDto> Route(int id, RouteRequest request, CancellationToken ct) =>
        await plans.RouteAsync(id, request, ct);

    /// <summary>Rule-based estimate of the rooms the fire may reach next, plus a risk heatmap grid.</summary>
    [HttpGet("{id:int}/risk")]
    public async Task<FireRiskDto> Risk(int id, CancellationToken ct) => await plans.RiskAsync(id, ct);
}
