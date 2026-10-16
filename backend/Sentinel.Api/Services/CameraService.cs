using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

public class CameraService(SentinelDbContext db) : ICameraService
{
    private static readonly System.Text.RegularExpressions.Regex CameraId = new("^[A-Za-z0-9_-]{1,64}$");
    private static readonly System.Text.RegularExpressions.Regex ZoneName = new(@"^\S.* - \S.*$");

    public async Task<IReadOnlyList<CameraFeedDto>> GetAllAsync(CancellationToken ct = default, bool revealCredentials = false) =>
        (await db.CameraFeeds.AsNoTracking().OrderBy(c => c.FeedType).ThenBy(c => c.Name).ToListAsync(ct))
        .Select(c => c.ToDto(revealCredentials)).ToList();

    public async Task<CameraFeedDto> CreateAsync(SaveCameraRequest request, CancellationToken ct = default)
    {
        var id = request.Id?.Trim() ?? "";
        if (!CameraId.IsMatch(id))
            throw new AppValidationException("id", "Id: 1–64 letters, digits, '-' or '_' (e.g. cam-gate). The AI service sends frames with this id.");
        if (await db.CameraFeeds.AnyAsync(c => c.Id == id, ct)) throw new AppValidationException("id", $"A camera with id '{id}' already exists.");
        var camera = new CameraFeed { Id = id };
        Apply(camera, request);
        db.CameraFeeds.Add(camera);
        await db.SaveChangesAsync(ct);
        return camera.ToDto();
    }

    public async Task<CameraFeedDto> UpdateAsync(string id, SaveCameraRequest request, CancellationToken ct = default)
    {
        var camera = await db.CameraFeeds.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException($"Camera '{id}' not found.");
        // The dashboard only ever sees the masked link; sending it back unchanged must not overwrite the real credentials.
        if (request.StreamUrl is not null && request.StreamUrl == Mapping.MaskCredentials(camera.StreamUrl)) request.StreamUrl = camera.StreamUrl;
        Apply(camera, request);
        camera.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return camera.ToDto();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var camera = await db.CameraFeeds.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException($"Camera '{id}' not found.");
        db.CameraFeeds.Remove(camera);
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(CameraFeed camera, SaveCameraRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        var name = r.Name?.Trim() ?? "";
        var zone = r.LocationZone?.Trim() ?? "";
        var url = r.StreamUrl?.Trim() ?? "";
        if (name.Length is 0 or > 120) errors["name"] = ["Name is required (max 120 characters)."];
        if (zone.Length > 64 || !ZoneName.IsMatch(zone)) errors["locationZone"] = ["Zone must be \"Floor - Area\", e.g. \"Ground Floor - Lobby\" (max 64 characters)."];
        if (url.Length > 500 || (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "rtsp" or "rtsps" or "http" or "https")))
            errors["streamUrl"] = ["Stream link must be an rtsp://, http:// or https:// address (max 500), or empty for a simulated feed."];
        if (!Enum.IsDefined(r.FeedType)) errors["feedType"] = ["Unknown camera type."];
        if (!Enum.IsDefined(r.StreamFormat)) errors["streamFormat"] = ["Unknown stream format."];
        if (errors.Count > 0) throw new AppValidationException(errors);
        camera.Name = name;
        camera.LocationZone = zone;
        camera.StreamUrl = url;
        camera.FeedType = r.FeedType;
        camera.StreamFormat = r.StreamFormat;
    }

    public async Task<CameraFeed> GetEntityAsync(string id, CancellationToken ct = default) =>
        await db.CameraFeeds.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new NotFoundException($"Camera '{id}' not found.");

    public async Task<CameraFeedDto> SaveCalibrationAsync(string id, CameraCalibrationDto? calibration, CancellationToken ct = default)
    {
        var camera = await db.CameraFeeds.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException($"Camera '{id}' not found.");
        if (calibration is not null)
        {
            var plan = await db.FloorPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == calibration.FloorPlanId, ct)
                ?? throw new AppValidationException("floorPlanId", "Floor plan not found.");
            CheckPoints("imagePoints", calibration.ImagePoints, double.MaxValue, double.MaxValue);
            CheckPoints("planPoints", calibration.PlanPoints, plan.WidthMeters, plan.HeightMeters);
        }
        camera.Calibration = calibration is null ? null : JsonSerializer.Serialize(calibration);
        camera.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return camera.ToDto();
    }

    // 4 point pairs fix the camera-to-plan mapping only if no 3 points of a set lie on one line.
    private static void CheckPoints(string field, double[][]? points, double maxX, double maxY)
    {
        if (points is not { Length: 4 } || points.Any(p => p is not { Length: 2 } || !p.All(double.IsFinite)))
            throw new AppValidationException(field, "Give exactly 4 points, each [x, y].");
        if (points.Any(p => p[0] < 0 || p[1] < 0 || p[0] > maxX || p[1] > maxY))
            throw new AppValidationException(field, "A point lies outside the camera frame or the floor plan.");
        for (var skip = 0; skip < 4; skip++)
        {
            var t = points.Where((_, k) => k != skip).ToArray();
            var area = Math.Abs((t[1][0] - t[0][0]) * (t[2][1] - t[0][1]) - (t[2][0] - t[0][0]) * (t[1][1] - t[0][1])) / 2;
            if (area < 1e-6) throw new AppValidationException(field, "Three of the 4 points lie on one line. Pick 4 corners of a floor area.");
        }
    }
}
