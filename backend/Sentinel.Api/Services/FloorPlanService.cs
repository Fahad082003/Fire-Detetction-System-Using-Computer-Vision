using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>Floor plans: CRUD with geometry validation, reference-image upload, and evacuation route estimates.</summary>
public class FloorPlanService(
    SentinelDbContext db,
    IAlertEngine alerts,
    IOptions<FloorPlanOptions> options,
    IWebHostEnvironment env) : IFloorPlanService
{
    private readonly FloorPlanOptions _opt = options.Value;

    public async Task<IReadOnlyList<FloorPlanDto>> ListAsync(CancellationToken ct = default) =>
        (await db.FloorPlans.AsNoTracking().Include(p => p.Elements)
            .OrderBy(p => p.Building).ThenBy(p => p.FloorNumber).ThenBy(p => p.Name).ToListAsync(ct))
        .Select(p => p.ToDto()).ToList();

    public async Task<FloorPlanDto> GetAsync(int id, CancellationToken ct = default) => (await FindAsync(id, ct)).ToDto();

    public async Task<FloorPlanDto> CreateAsync(SaveFloorPlanRequest request, CancellationToken ct = default)
    {
        var plan = new FloorPlan();
        Apply(plan, request);
        db.FloorPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        return plan.ToDto();
    }

    public async Task<FloorPlanDto> UpdateAsync(int id, SaveFloorPlanRequest request, CancellationToken ct = default)
    {
        var plan = await FindAsync(id, ct);
        db.FloorPlanElements.RemoveRange(plan.Elements);
        plan.Elements.Clear();
        Apply(plan, request);
        plan.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return plan.ToDto();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var plan = await FindAsync(id, ct);
        DeleteImageFile(plan.ImageFileName);
        db.FloorPlans.Remove(plan);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FloorPlanDto> SaveImageAsync(int id, IFormFile file, CancellationToken ct = default)
    {
        var plan = await FindAsync(id, ct);
        if (file is null || file.Length == 0) throw new AppValidationException("file", "Choose a PNG or JPG image.");
        if (file.Length > _opt.MaxImageBytes)
            throw new AppValidationException("file", $"Image is larger than {_opt.MaxImageBytes / (1024 * 1024)} MB.");

        // Trust the file's magic bytes, not its name or the browser-supplied content type.
        var head = new byte[8];
        await using (var s = file.OpenReadStream()) _ = await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var ext = ImageExtension(head) ?? throw new AppValidationException("file", "Only PNG or JPG images are allowed.");

        var dir = ImageDirectory();
        Directory.CreateDirectory(dir);
        var fileName = $"{plan.Id}{ext}"; // server-chosen name: no user input reaches the file-system path
        await using (var target = File.Create(Path.Combine(dir, fileName))) await file.CopyToAsync(target, ct);
        if (plan.ImageFileName is not null && plan.ImageFileName != fileName) DeleteImageFile(plan.ImageFileName);

        plan.ImageFileName = fileName;
        plan.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return plan.ToDto();
    }

    public async Task<(string Path, string ContentType)> GetImageAsync(int id, CancellationToken ct = default)
    {
        var plan = await FindAsync(id, ct);
        var path = plan.ImageFileName is null ? null : Path.Combine(ImageDirectory(), plan.ImageFileName);
        if (path is null || !File.Exists(path)) throw new NotFoundException($"Floor plan #{id} has no image.");
        return (path, path.EndsWith(".png") ? "image/png" : "image/jpeg");
    }

    public async Task<RouteResultDto> RouteAsync(int id, RouteRequest request, CancellationToken ct = default)
    {
        var plan = await FindAsync(id, ct);
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y) || request.X < 0 || request.Y < 0
            || request.X > plan.WidthMeters || request.Y > plan.HeightMeters)
            throw new AppValidationException("start", "Start point must be inside the floor plan.");

        var active = await ActiveDangerAsync(plan, ct);
        var r = EvacuationRouter.FindRoute(plan.WidthMeters, plan.HeightMeters, plan.Elements.ToList(), active, request.X, request.Y, _opt.DangerClearanceMeters);
        var speed = _opt.WalkingSpeedMetersPerSecond;
        var seconds = r.Found ? Math.Round(r.DistanceMeters / speed, 1) : 0;

        // Log the estimate (requirement 5.3), skipping an exact repeat of the previous entry for this plan.
        var logZone = $"Floor plan #{plan.Id} {plan.Name}";
        var details = r.Found
            ? $"ESTIMATE: route from ({request.X:0.#}, {request.Y:0.#}) m to exit '{r.ExitName}': {r.DistanceMeters:0.#} m, ~{seconds:0} s at {speed} m/s"
              + (r.ClearanceViolated ? $" | CLEARANCE VIOLATED: passes within {r.MinDangerGapMeters:0.#} m of an active danger zone (required {_opt.DangerClearanceMeters:0.#} m)" : "")
            : $"No safe route from ({request.X:0.#}, {request.Y:0.#}) m: {r.Message}";
        var last = await db.IncidentLogs.Where(l => l.Type == IncidentLogType.EvacuationRoute && l.Zone == logZone)
            .OrderByDescending(l => l.Id).Select(l => l.Details).FirstOrDefaultAsync(ct);
        if (last != details)
            await alerts.AddLogAsync(new IncidentLog { Type = IncidentLogType.EvacuationRoute, Zone = logZone, Details = details }, ct);

        return new RouteResultDto(r.Found, r.Message, r.Path, r.DistanceMeters, seconds, speed, r.ExitName, r.StartInDangerZone,
            active.Select(e => string.IsNullOrWhiteSpace(e.Name) ? "Danger zone" : e.Name).ToList(),
            r.ClearanceViolated, r.MinDangerGapMeters, _opt.DangerClearanceMeters);
    }

    public async Task<FireRiskDto> RiskAsync(int id, CancellationToken ct = default)
    {
        var plan = await FindAsync(id, ct);
        return FireRiskEstimator.Estimate(plan.WidthMeters, plan.HeightMeters, plan.Elements.ToList(), await ActiveDangerAsync(plan, ct));
    }

    /// <summary>A danger zone is active when marked so, or when its linked camera/sensor zone has an open incident.</summary>
    private async Task<List<FloorPlanElement>> ActiveDangerAsync(FloorPlan plan, CancellationToken ct)
    {
        var openZones = (await db.IncidentAlerts.Where(i => !i.IsResolved).Select(i => i.Zone).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Elements.Where(e => e.Type == FloorElementType.DangerZone
            && (e.IsActive || (!string.IsNullOrWhiteSpace(e.Zone) && openZones.Contains(e.Zone)))).ToList();
    }

    /// <summary>".png" / ".jpg" from the file's magic bytes, or null for anything else.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> head) =>
        head is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] ? ".png" : head is [0xFF, 0xD8, 0xFF, ..] ? ".jpg" : null;

    private async Task<FloorPlan> FindAsync(int id, CancellationToken ct) =>
        await db.FloorPlans.Include(p => p.Elements).FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new NotFoundException($"Floor plan #{id} not found.");

    /// <summary>Copies the request onto the entity after checking every element lies inside the plan.</summary>
    private static void Apply(FloorPlan plan, SaveFloorPlanRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < r.Elements.Count; i++)
        {
            var e = r.Elements[i];
            if (!Enum.IsDefined(e.Type)) errors[$"elements[{i}].type"] = ["Unknown element type."];
            else if (!new[] { e.X, e.Y, e.Width, e.Height }.All(double.IsFinite) || e.Width <= 0 || e.Height <= 0)
                errors[$"elements[{i}]"] = ["Width and height must be positive numbers."];
            else if (e.X < 0 || e.Y < 0 || e.X + e.Width > r.WidthMeters + 0.001 || e.Y + e.Height > r.HeightMeters + 0.001)
                errors[$"elements[{i}]"] = [$"'{e.Name}' lies outside the {r.WidthMeters} x {r.HeightMeters} m plan."];
        }
        if (errors.Count > 0) throw new AppValidationException(errors);

        plan.Name = r.Name.Trim();
        plan.Building = r.Building?.Trim() ?? string.Empty;
        plan.FloorNumber = r.FloorNumber;
        plan.WidthMeters = r.WidthMeters;
        plan.HeightMeters = r.HeightMeters;
        foreach (var e in r.Elements)
        {
            plan.Elements.Add(new FloorPlanElement
            {
                Type = e.Type, Name = e.Name.Trim(), X = e.X, Y = e.Y, Width = e.Width, Height = e.Height,
                IsBlocked = e.IsBlocked, IsActive = e.IsActive, Zone = string.IsNullOrWhiteSpace(e.Zone) ? null : e.Zone.Trim(),
            });
        }
    }

    private string ImageDirectory() => Path.Combine(env.ContentRootPath, _opt.ImageDirectory);

    private void DeleteImageFile(string? fileName)
    {
        if (fileName is null) return;
        var path = Path.Combine(ImageDirectory(), fileName);
        if (File.Exists(path)) File.Delete(path);
    }
}
