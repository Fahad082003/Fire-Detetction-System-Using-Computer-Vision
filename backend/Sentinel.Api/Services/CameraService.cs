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
    public async Task<IReadOnlyList<CameraFeedDto>> GetAllAsync(CancellationToken ct = default) =>
        (await db.CameraFeeds.AsNoTracking().OrderBy(c => c.FeedType).ThenBy(c => c.Name).ToListAsync(ct))
        .Select(c => c.ToDto()).ToList();

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
