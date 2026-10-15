using System.Text.RegularExpressions;
using Sentinel.Api.DTOs;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;

namespace Sentinel.Api.Validators;

/// <summary>
/// Single source of truth for AI payload validation. Used by the REST endpoint and the SignalR hub,
/// so both ingestion paths accept exactly the same frames. Also derives missing counts.
/// </summary>
public static partial class DetectionPayloadValidator
{
    public const int MaxDetections = 200;
    public const int MaxZones = 32;
    public const int MaxPolygonPoints = 64;
    public const int MaxHeatmapSide = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    public static DetectionFrameDto Validate(DetectionPayloadDto? payload)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        if (payload is null)
            throw new AppValidationException("payload", "Payload is required.");

        var detections = payload.Detections ?? [];
        var zones = payload.DangerZones ?? [];
        var points = payload.HumanCoordinates ?? [];

        if (string.IsNullOrWhiteSpace(payload.CameraId) || !IdPattern().IsMatch(payload.CameraId))
            Add("cameraId", "cameraId is required (letters, digits, '-' or '_', max 64).");
        if (payload.FrameWidth is < 1 or > 16384) Add("frameWidth", "Must be between 1 and 16384.");
        if (payload.FrameHeight is < 1 or > 16384) Add("frameHeight", "Must be between 1 and 16384.");

        if (detections.Count > MaxDetections) Add("detections", $"At most {MaxDetections} detections per frame.");
        for (var i = 0; i < Math.Min(detections.Count, MaxDetections); i++)
        {
            var d = detections[i];
            if (d is null) { Add($"detections[{i}]", "Must not be null."); continue; }
            if (d.Bbox is null || d.Bbox.Length != 4) Add($"detections[{i}].bbox", "bbox must be [x, y, width, height].");
            else if (d.Bbox.Any(v => !double.IsFinite(v)) || d.Bbox[2] < 0 || d.Bbox[3] < 0)
                Add($"detections[{i}].bbox", "bbox values must be finite and width/height non-negative.");
            if (d.Confidence is < 0 or > 1) Add($"detections[{i}].confidence", "Must be between 0 and 1.");
            if (d.Label?.Length > 48) Add($"detections[{i}].label", "Max 48 characters.");
            if (d.TrackId?.Length > 48) Add($"detections[{i}].trackId", "Max 48 characters.");
            if (d.Explanation?.Length > 240) Add($"detections[{i}].explanation", "Max 240 characters.");
        }

        if (zones.Count > MaxZones) Add("dangerZones", $"At most {MaxZones} zones per frame.");
        for (var i = 0; i < Math.Min(zones.Count, MaxZones); i++)
        {
            var z = zones[i];
            if (z?.Polygon is null || z.Polygon.Count is < 3 or > MaxPolygonPoints)
                Add($"dangerZones[{i}].polygon", $"Polygon needs 3 to {MaxPolygonPoints} points.");
            else if (z.Polygon.Any(p => p is null || p.Length != 2 || !p.All(double.IsFinite)))
                Add($"dangerZones[{i}].polygon", "Every point must be [x, y].");
            if (z?.Label?.Length > 48) Add($"dangerZones[{i}].label", "Max 48 characters.");
        }

        if (payload.Heatmap is { } h)
        {
            if (h.Rows is < 1 or > MaxHeatmapSide || h.Cols is < 1 or > MaxHeatmapSide)
                Add("heatmap", $"rows and cols must be between 1 and {MaxHeatmapSide}.");
            else if (h.Values is null || h.Values.Length != h.Rows * h.Cols)
                Add("heatmap.values", "values length must equal rows * cols (row-major).");
            else if (h.Values.Any(v => v is < 0 or > 1 || double.IsNaN(v)))
                Add("heatmap.values", "Every value must be between 0 and 1.");
        }

        if (points.Count > MaxDetections) Add("humanCoordinates", $"At most {MaxDetections} points.");
        else if (points.Any(p => p is null || p.Length != 2 || !p.All(double.IsFinite)))
            Add("humanCoordinates", "Every point must be [x, y].");

        if (payload.Model?.Length > 64) Add("model", "Max 64 characters.");
        if (payload.HumanCount < 0) Add("humanCount", "Must be zero or more.");
        if (payload.DangerCount < 0) Add("dangerCount", "Must be zero or more.");

        if (errors.Count > 0)
            throw new AppValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

        // ---- requirement 2.3: the backend itself decides who is in danger. A person whose feet
        // (bottom-centre of the box) stand inside a Danger/Critical zone is at risk, even if the AI did not say so.
        foreach (var d in detections.Where(d => d.Category == DetectionCategory.Human))
        {
            double fx = d.Bbox[0] + d.Bbox[2] / 2, fy = d.Bbox[1] + d.Bbox[3];
            var zone = zones.FirstOrDefault(z => z.Level is ZoneLevel.Danger or ZoneLevel.Critical && Contains(z.Polygon, fx, fy));
            if (zone is null) continue;
            d.AtRisk = true;
            d.DangerZone = string.IsNullOrWhiteSpace(zone.Label) ? $"{zone.Level} zone" : zone.Label;
        }

        // ---- derive values the producer omitted
        var fire = payload.FireDetected ?? detections.Any(d => d.Category == DetectionCategory.Fire);
        var humans = payload.HumanCount
                     ?? (detections.Count(d => d.Category == DetectionCategory.Human) is var n and > 0 ? n : points.Count);
        // Never report fewer people in danger than the backend's own zone check found.
        var danger = Math.Max(payload.DangerCount ?? 0,
                              detections.Count(d => d.Category == DetectionCategory.Danger || d.AtRisk));
        var timestamp = payload.Timestamp is { } ts ? DateTime.SpecifyKind(ts.ToUniversalTime(), DateTimeKind.Utc) : DateTime.UtcNow;

        return new DetectionFrameDto(
            payload.CameraId, timestamp, payload.FrameWidth, payload.FrameHeight, payload.Normalized,
            detections, zones, payload.Heatmap, points, fire, fire ? payload.FireSeverity : null, humans, danger, payload.Simulated,
            detections.Any(d => d.Category == DetectionCategory.Smoke), payload.Model);
    }

    /// <summary>Ray-casting point-in-polygon test (polygon given as [x, y] points).</summary>
    public static bool Contains(IReadOnlyList<double[]> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i][0], yi = polygon[i][1], xj = polygon[j][0], yj = polygon[j][1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}
