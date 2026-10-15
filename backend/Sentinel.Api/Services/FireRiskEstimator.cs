using Sentinel.Api.DTOs;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

/// <summary>
/// Requirement 3.3 / 3.4: which areas a fire may reach next, and a risk heatmap of the floor.
/// RULE-BASED ESTIMATE, not a fire-spread model: it ranks every place by the shortest path from an active danger zone
/// through rooms, corridors and doors. Walls block it; doors do not, closed ones included (they slow a fire, they do
/// not stop it). Levels: Burning (inside the zone), High ≤ 5 m, Medium ≤ 10 m, Low ≤ 20 m, None beyond or unreachable.
/// Cells: -1 outside the building, 0 none, 1 low, 2 medium, 3 high, 4 burning (row-major, CellMeters square).
/// </summary>
// shortcut: distance only, no material, ventilation, smoke or time model; replace with a trained model (e.g. ConvLSTM) when data exists.
public static class FireRiskEstimator
{
    public const double HighMeters = 5, MediumMeters = 10, LowMeters = 20;
    private const double BaseCellMeters = 1;
    private const int MaxCells = 20_000;
    private static readonly string[] LevelNames = ["None", "Low", "Medium", "High", "Burning"];

    public static FireRiskDto Estimate(double width, double height, IReadOnlyList<FloorPlanElement> elements,
        IReadOnlyCollection<FloorPlanElement> activeDanger)
    {
        var cell = Math.Max(BaseCellMeters, Math.Sqrt(width * height / MaxCells));
        int cols = (int)Math.Ceiling(width / cell), rows = (int)Math.Ceiling(height / cell), n = cols * rows;
        var dangerNames = activeDanger.Select(z => string.IsNullOrWhiteSpace(z.Name) ? "Danger zone" : z.Name).ToList();
        var cells = new int[n];
        Array.Fill(cells, -1);
        if (activeDanger.Count == 0)
            return new FireRiskDto(false, "No active fire on this floor, so there is nothing to predict.", cols, rows, cell, cells, [], dangerNames);

        var spaces = elements.Where(e => e.Type is FloorElementType.Room or FloorElementType.Corridor or FloorElementType.Door
            or FloorElementType.Exit or FloorElementType.SafeZone or FloorElementType.DangerZone).ToList();
        var walls = elements.Where(e => e.Type == FloorElementType.Wall).ToList();
        var doors = elements.Where(e => e.Type is FloorElementType.Door or FloorElementType.Exit).ToList();
        var open = new bool[n];
        var dist = new double[n];
        Array.Fill(dist, double.PositiveInfinity);
        var queue = new PriorityQueue<int, double>();
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            double x0 = c * cell, y0 = r * cell;
            if (!spaces.Any(s => EvacuationRouter.Inside(s, x0 + cell / 2, y0 + cell / 2))) continue;
            var i = r * cols + c;
            cells[i] = 0;
            if (walls.Any(w => EvacuationRouter.Overlaps(w, x0, y0, cell)) && !doors.Any(d => EvacuationRouter.Overlaps(d, x0, y0, cell))) continue;
            open[i] = true;
            if (activeDanger.Any(z => EvacuationRouter.Overlaps(z, x0, y0, cell))) { dist[i] = 0; queue.Enqueue(i, 0); }
        }

        while (queue.TryDequeue(out var u, out var du)) // multi-source Dijkstra, 8 neighbours, no corner cutting (as the router)
        {
            if (du > dist[u]) continue;
            int ur = u / cols, uc = u % cols;
            for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                int nr = ur + dr, nc = uc + dc;
                if ((dr == 0 && dc == 0) || nr < 0 || nc < 0 || nr >= rows || nc >= cols) continue;
                var v = nr * cols + nc;
                var diagonal = dr != 0 && dc != 0;
                if (!open[v] || (diagonal && (!open[ur * cols + nc] || !open[nr * cols + uc]))) continue;
                var nd = du + (diagonal ? Math.Sqrt(2) : 1) * cell;
                if (nd < dist[v]) { dist[v] = nd; queue.Enqueue(v, nd); }
            }
        }
        for (var i = 0; i < n; i++)
            if (open[i]) cells[i] = Level(dist[i]);

        var areas = new List<AreaRiskDto>();
        foreach (var e in elements.Where(e => e.Type is FloorElementType.Room or FloorElementType.Corridor))
        {
            var nearest = double.PositiveInfinity;
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                if (open[r * cols + c] && EvacuationRouter.Inside(e, (c + 0.5) * cell, (r + 0.5) * cell))
                    nearest = Math.Min(nearest, dist[r * cols + c]);
            var name = string.IsNullOrWhiteSpace(e.Name) ? e.Type.ToString() : e.Name;
            areas.Add(new AreaRiskDto(name, e.Type, LevelNames[Level(nearest)], double.IsFinite(nearest) ? Math.Round(nearest, 1) : null));
        }
        areas = [.. areas.OrderBy(a => a.DistanceMeters ?? double.MaxValue)];

        var next = areas.Where(a => a.Level is "High" or "Medium").Take(3).ToList();
        var message = next.Count > 0
            ? $"Next at risk: {string.Join(", ", next.Select(a => $"{a.Name} ({a.DistanceMeters:0.#} m from the fire, {a.Level})"))}."
            : $"No other room or corridor within {MediumMeters:0} m of the fire.";
        return new FireRiskDto(true, message, cols, rows, cell, cells, areas, dangerNames);
    }

    private static int Level(double d) => d <= 0 ? 4 : d <= HighMeters ? 3 : d <= MediumMeters ? 2 : d <= LowMeters ? 1 : 0;
}
