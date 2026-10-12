using Sentinel.Api.Models;

namespace Sentinel.Api.Services;

public record RouteResult(bool Found, string Message, List<double[]> Path, double DistanceMeters, string? ExitName, bool StartInDangerZone)
{
    public static RouteResult Fail(string message, bool startInDanger = false) => new(false, message, [], 0, null, startInDanger);
}

/// <summary>
/// Requirement 2.4: shortest safe walking route on a floor plan (meters). The plan is rasterised to a grid;
/// rooms, corridors, doors, exits and safe zones are walkable; walls (unless an open door/exit overlaps them),
/// blocked doors/exits and active danger zones are obstacles. Dijkstra with 8 neighbours finds the nearest
/// reachable open exit, so a blocked nearest exit automatically yields the next reachable one.
/// </summary>
public static class EvacuationRouter
{
    private const double BaseCellMeters = 0.5;
    private const int MaxCells = 200_000;

    public static RouteResult FindRoute(double width, double height, IReadOnlyList<FloorPlanElement> elements,
        IReadOnlyCollection<FloorPlanElement> activeDanger, double startX, double startY)
    {
        if (!elements.Any(e => e.Type is FloorElementType.Room or FloorElementType.Corridor))
            return RouteResult.Fail("Floor plan has no rooms or corridors, so no walkable area is defined.");
        var exits = elements.Where(e => e.Type == FloorElementType.Exit && !e.IsBlocked).ToList();
        if (exits.Count == 0) return RouteResult.Fail("Floor plan has no open emergency exit.");

        // A person already inside a danger zone must be allowed to walk out of it.
        var startDanger = activeDanger.Where(z => Inside(z, startX, startY)).ToList();
        var dangers = activeDanger.Except(startDanger).ToList();
        var walkAreas = elements.Where(e => !e.IsBlocked && e.Type is FloorElementType.Room or FloorElementType.Corridor
            or FloorElementType.Door or FloorElementType.Exit or FloorElementType.SafeZone).ToList();
        var walls = elements.Where(e => e.Type == FloorElementType.Wall).ToList();
        var openings = elements.Where(e => !e.IsBlocked && e.Type is FloorElementType.Door or FloorElementType.Exit).ToList();
        var closed = elements.Where(e => e.IsBlocked && e.Type is FloorElementType.Door or FloorElementType.Exit).ToList();

        var cell = Math.Max(BaseCellMeters, Math.Sqrt(width * height / MaxCells));
        int cols = (int)Math.Ceiling(width / cell), rows = (int)Math.Ceiling(height / cell), n = cols * rows;
        var free = new bool[n];
        var goal = new bool[n];
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            double x0 = c * cell, y0 = r * cell, cx = x0 + cell / 2, cy = y0 + cell / 2;
            if (!walkAreas.Any(a => Inside(a, cx, cy))) continue;
            if (closed.Any(o => Overlaps(o, x0, y0, cell))) continue;
            // Walls are thin, so test overlap with the whole cell (a centre test would let routes slip through).
            if (walls.Any(w => Overlaps(w, x0, y0, cell)) && !openings.Any(o => Overlaps(o, x0, y0, cell))) continue;
            if (dangers.Any(z => Overlaps(z, x0, y0, cell))) continue;
            var i = r * cols + c;
            free[i] = true;
            goal[i] = exits.Any(e => Inside(e, cx, cy));
        }

        int sc = Math.Clamp((int)(startX / cell), 0, cols - 1), sr = Math.Clamp((int)(startY / cell), 0, rows - 1);
        var start = sr * cols + sc;
        if (!free[start])
        {
            return RouteResult.Fail(walkAreas.Any(a => Inside(a, startX, startY))
                ? "Start point is on a wall, a closed door or inside an active danger zone."
                : "Start point is not inside a room, corridor or other walkable area.", startDanger.Count > 0);
        }

        var dist = new double[n];
        var prev = new int[n];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(prev, -1);
        var queue = new PriorityQueue<int, double>();
        dist[start] = 0;
        queue.Enqueue(start, 0);
        var reached = -1;
        while (queue.TryDequeue(out var u, out var du))
        {
            if (du > dist[u]) continue;
            if (goal[u]) { reached = u; break; }
            int ur = u / cols, uc = u % cols;
            for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nr = ur + dr, nc = uc + dc;
                if (nr < 0 || nc < 0 || nr >= rows || nc >= cols) continue;
                var v = nr * cols + nc;
                if (!free[v]) continue;
                var diagonal = dr != 0 && dc != 0;
                if (diagonal && (!free[ur * cols + nc] || !free[nr * cols + uc])) continue; // no corner cutting
                var nd = du + (diagonal ? Math.Sqrt(2) : 1) * cell;
                if (nd < dist[v]) { dist[v] = nd; prev[v] = u; queue.Enqueue(v, nd); }
            }
        }
        if (reached < 0)
            return RouteResult.Fail("No safe route: every exit is cut off by walls, closed doors or active danger zones.", startDanger.Count > 0);

        // Cell path → points; keep only turning points so the polyline stays small.
        var cells = new List<int>();
        for (var i = reached; i >= 0; i = prev[i]) cells.Add(i);
        cells.Reverse();
        var path = new List<double[]> { new[] { Math.Round(startX, 2), Math.Round(startY, 2) } };
        for (var k = 1; k < cells.Count; k++)
        {
            var isLast = k == cells.Count - 1;
            if (!isLast && cells[k] - cells[k - 1] == cells[k + 1] - cells[k]) continue; // same direction: not a turn
            path.Add(new[] { Math.Round((cells[k] % cols + 0.5) * cell, 2), Math.Round((cells[k] / cols + 0.5) * cell, 2) });
        }
        var distance = 0.0;
        for (var k = 1; k < path.Count; k++) distance += Math.Sqrt(Math.Pow(path[k][0] - path[k - 1][0], 2) + Math.Pow(path[k][1] - path[k - 1][1], 2));

        double gx = (reached % cols + 0.5) * cell, gy = (reached / cols + 0.5) * cell;
        var exitName = exits.First(e => Inside(e, gx, gy)).Name;
        return new RouteResult(true, startDanger.Count > 0
                ? $"Route to exit '{exitName}'. Start point is inside an active danger zone: leave immediately."
                : $"Route to exit '{exitName}'.",
            path, Math.Round(distance, 2), exitName, startDanger.Count > 0);
    }

    public static bool Inside(FloorPlanElement e, double x, double y) =>
        x >= e.X && x <= e.X + e.Width && y >= e.Y && y <= e.Y + e.Height;

    private static bool Overlaps(FloorPlanElement e, double x0, double y0, double size) =>
        e.X < x0 + size && e.X + e.Width > x0 && e.Y < y0 + size && e.Y + e.Height > y0;
}
