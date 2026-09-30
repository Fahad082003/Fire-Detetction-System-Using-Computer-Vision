// Sentinel field simulator — stands in for the external systems that feed the platform:
//   * AI vision pipeline -> SignalR hub method PublishDetection (bounding boxes, danger zones, fire severity)
//   * IoT gateway        -> REST POST /api/iot/telemetry (smoke ppm / temperature °C)
//   * Sprinkler relays   -> REST GET  /api/iot/sprinklers (active relays suppress the simulated fire)
//
// Usage:
//   dotnet run --project Sentinel.Simulator -- --device-key <key> --ignite "Zone B"
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sentinel.Simulator;

var options = SimulatorOptions.Parse(args);
if (options is null) return 1;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var sim = new FieldSimulator(options);
try
{
    await sim.RunAsync(cts.Token);
}
catch (OperationCanceledException) { }
catch (HttpRequestException ex)
{
    Log.Error($"Cannot reach the API at {options.BaseUrl}: {ex.Message}");
    return 2;
}
return 0;

namespace Sentinel.Simulator
{
    public sealed record SimulatorOptions(
        string BaseUrl, string DeviceKey, double Fps, double TelemetryInterval, double FireProbability, List<string> Ignite)
    {
        public static SimulatorOptions? Parse(string[] args)
        {
            string baseUrl = "http://localhost:5291", key = Environment.GetEnvironmentVariable("Device__ApiKey") ?? "";
            double fps = 5, telemetry = 2, fireProb = 0.004;
            var ignite = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
                switch (args[i])
                {
                    case "--base-url": baseUrl = Next().TrimEnd('/'); break;
                    case "--device-key": key = Next(); break;
                    case "--fps": fps = double.Parse(Next()); break;
                    case "--telemetry-interval": telemetry = double.Parse(Next()); break;
                    case "--fire-probability": fireProb = double.Parse(Next()); break;
                    case "--ignite": ignite.Add(Next()); break;
                    case "-h" or "--help":
                        Console.WriteLine("""
                            Sentinel simulator
                              --base-url URL            API base (default http://localhost:5291)
                              --device-key KEY          Device__ApiKey configured on the API (or env Device__ApiKey)
                              --fps N                   Detection frames per second per camera (default 5)
                              --telemetry-interval S    Seconds between sensor batches (default 2)
                              --fire-probability P      Random ignition chance per zone per second (default 0.004)
                              --ignite ZONE             Start a fire in ZONE immediately (repeatable)
                            """);
                        return null;
                    default: Log.Error($"Unknown argument {args[i]} (use --help)"); return null;
                }
            }
            if (string.IsNullOrWhiteSpace(key))
            {
                Log.Error("A device key is required: --device-key <key> (must match Device__ApiKey on the API).");
                return null;
            }
            return new SimulatorOptions(baseUrl, key, fps, telemetry, fireProb, ignite);
        }
    }

    // ------------------------------------------------------------------ API contracts (as an external client sees them)
    public record CameraDto(string Id, string Name, string FeedType, string LocationZone);
    public record SensorDto(string Id, string Type, string LocationZone);
    public record SprinklerDto(string Id, string Zone, bool IsActive);
    public record HubResult(bool Ok, string? Error, int? IncidentId);

    // ------------------------------------------------------------------ world model
    public sealed class ZoneFire
    {
        public double Intensity;                 // 0..1
        public (double X, double Y) Center = (0.5, 0.6);
        public bool Suppressing;
        public bool Burning => Intensity > 0.05;

        /// <summary>AI-style severity level derived from intensity.</summary>
        public string Level => Intensity switch { < 0.3 => "Low", < 0.55 => "Medium", < 0.8 => "High", _ => "Critical" };
    }

    public sealed class Walker(string trackId, double x, double y, double vx, double vy)
    {
        public string TrackId { get; } = trackId;
        public double X = x, Y = y, Vx = vx, Vy = vy;

        public void Step(double dt, (double X, double Y)? fleeFrom, Random rng)
        {
            if (fleeFrom is { } f)
            {
                double dx = X - f.X, dy = Y - f.Y, dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1e-3);
                Vx += dx / dist * 0.25 * dt;
                Vy += dy / dist * 0.12 * dt;
            }
            var speed = Math.Sqrt(Vx * Vx + Vy * Vy);
            if (speed > 0.18) { Vx = Vx / speed * 0.18; Vy = Vy / speed * 0.18; }
            Vx += (rng.NextDouble() - 0.5) * 0.04;
            Vy += (rng.NextDouble() - 0.5) * 0.02;
            X += Vx * dt;
            Y += Vy * dt;
            if (X is < 0.04 or > 0.92) { Vx = -Vx; X = Math.Clamp(X, 0.04, 0.92); }
            if (Y is < 0.35 or > 0.78) { Vy = -Vy; Y = Math.Clamp(Y, 0.35, 0.78); }
        }
    }

    public sealed class CameraSim(string id, string zone, int width, int height, List<Walker> walkers)
    {
        public string Id { get; } = id;
        public string Zone { get; } = zone;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public List<Walker> Walkers { get; } = walkers;

        public static CameraSim Create(CameraDto c, Random rng)
        {
            var drone = c.FeedType == "Drone";
            var suffix = c.Id.Length > 3 ? c.Id[^3..] : c.Id;
            var walkers = Enumerable.Range(1, rng.Next(1, 5)).Select(i => new Walker(
                $"{suffix}-{i}", 0.1 + rng.NextDouble() * 0.75, 0.4 + rng.NextDouble() * 0.35,
                (rng.NextDouble() - 0.5) * 0.16, (rng.NextDouble() - 0.5) * 0.06)).ToList();
            return new CameraSim(c.Id, c.LocationZone, drone ? 1920 : 1280, drone ? 1080 : 720, walkers);
        }
    }

    // ------------------------------------------------------------------ runtime
    public sealed class FieldSimulator(SimulatorOptions opt)
    {
        private const int HeatCols = 32, HeatRows = 18;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        private readonly Random _rng = new();
        private readonly Dictionary<string, ZoneFire> _fires = new();
        private readonly object _gate = new();
        private HashSet<string> _activeSprinklerZones = new();
        private List<CameraSim> _cameras = [];
        private List<SensorDto> _sensors = [];
        private HttpClient _http = null!;

        private ZoneFire Fire(string zone)
        {
            lock (_gate) return _fires.TryGetValue(zone, out var f) ? f : _fires[zone] = new ZoneFire();
        }

        private void Ignite(string zone)
        {
            var f = Fire(zone);
            lock (_gate)
            {
                f.Intensity = Math.Max(f.Intensity, 0.12);
                f.Center = (0.25 + _rng.NextDouble() * 0.5, 0.5 + _rng.NextDouble() * 0.2);
                f.Suppressing = false;
            }
            Log.Warn($"FIRE ignited in {zone}");
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _http = new HttpClient { BaseAddress = new Uri(opt.BaseUrl + "/") };
            _http.DefaultRequestHeaders.Add("X-Device-Key", opt.DeviceKey);

            var cameras = await GetAsync<List<CameraDto>>("api/cameras", ct);
            _sensors = await GetAsync<List<SensorDto>>("api/iot/sensors", ct);
            _cameras = cameras.Select(c => CameraSim.Create(c, _rng)).ToList();
            if (_cameras.Count == 0 && _sensors.Count == 0)
                throw new InvalidOperationException("No cameras or sensors registered. Start the API in Development (Seed:DemoData=true).");
            Log.Info($"Simulating {_cameras.Count} camera(s) and {_sensors.Count} sensor(s)");
            foreach (var z in opt.Ignite) Ignite(z);

            await Task.WhenAll(WorldLoop(ct), SprinklerLoop(ct), TelemetryLoop(ct), VisionLoop(ct));
        }

        private async Task<T> GetAsync<T>(string path, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"GET {path} returned {(int)resp.StatusCode}. Is the device key correct?");
            return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }

        private IEnumerable<string> Zones() =>
            _cameras.Select(c => c.Zone).Concat(_sensors.Select(s => s.LocationZone)).Distinct();

        /// <summary>Fire physics: random ignition, logistic growth, suppression while the zone's sprinklers are on.</summary>
        private async Task WorldLoop(CancellationToken ct)
        {
            const double dt = 0.5;
            while (!ct.IsCancellationRequested)
            {
                foreach (var zone in Zones())
                {
                    var f = Fire(zone);
                    if (!f.Burning && _rng.NextDouble() < opt.FireProbability * dt) Ignite(zone);
                    lock (_gate)
                    {
                        if (!f.Burning) { f.Intensity = 0; continue; }
                        if (_activeSprinklerZones.Contains(zone))
                        {
                            f.Intensity = Math.Max(0, f.Intensity - 0.025 * dt);
                            if (!f.Suppressing) { f.Suppressing = true; Log.Info($"Sprinklers suppressing fire in {zone}"); }
                            if (!f.Burning) Log.Info($"Fire in {zone} extinguished");
                        }
                        else
                        {
                            f.Intensity = Math.Min(1, f.Intensity + 0.035 * dt * (1.2 - f.Intensity));
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(dt), ct);
            }
        }

        private async Task SprinklerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var list = await GetAsync<List<SprinklerDto>>("api/iot/sprinklers", ct);
                    _activeSprinklerZones = list.Where(s => s.IsActive).Select(s => s.Zone).ToHashSet();
                }
                catch (HttpRequestException ex) { Log.Warn($"Sprinkler poll failed: {ex.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }

        private async Task TelemetryLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var readings = _sensors.Select(s =>
                {
                    var f = Fire(s.LocationZone);
                    var noise = Gaussian();
                    var value = s.Type == "Temperature"
                        ? Math.Round(22 + f.Intensity * 75 + noise * 0.6, 2)
                        : Math.Round(Math.Max(0, 15 + f.Intensity * 620 + noise * 4), 1);
                    return new { sensorId = s.Id, value };
                }).ToList();

                if (readings.Count > 0)
                {
                    try
                    {
                        using var resp = await _http.PostAsJsonAsync("api/iot/telemetry", new { readings }, Json, ct);
                        if (!resp.IsSuccessStatusCode) Log.Warn($"Telemetry rejected ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
                    }
                    catch (HttpRequestException ex) { Log.Warn($"Telemetry post failed: {ex.Message}"); }
                }
                await Task.Delay(TimeSpan.FromSeconds(opt.TelemetryInterval), ct);
            }
        }

        private async Task VisionLoop(CancellationToken ct)
        {
            var hub = new HubConnectionBuilder()
                .WithUrl($"{opt.BaseUrl}/hubs/monitoring", o => o.Headers.Add("X-Device-Key", opt.DeviceKey))
                .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .WithAutomaticReconnect()
                .Build();
            hub.Reconnecting += _ => { Log.Warn("Hub connection lost — reconnecting…"); return Task.CompletedTask; };
            hub.Reconnected += _ => { Log.Info("Hub reconnected"); return Task.CompletedTask; };

            var backoff = 1;
            while (!ct.IsCancellationRequested && hub.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await hub.StartAsync(ct);
                    Log.Info($"Vision producer connected to {opt.BaseUrl}/hubs/monitoring");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Hub connect failed: {ex.Message} — retrying in {backoff}s");
                    await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
                    backoff = Math.Min(backoff * 2, 30);
                }
            }

            var interval = 1.0 / opt.Fps;
            while (!ct.IsCancellationRequested)
            {
                if (hub.State == HubConnectionState.Connected)
                {
                    foreach (var cam in _cameras)
                    {
                        try
                        {
                            var result = await hub.InvokeAsync<HubResult>("PublishDetection", BuildFrame(cam, interval), ct);
                            if (!result.Ok) Log.Warn($"Frame from {cam.Id} rejected: {result.Error}");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Warn($"PublishDetection failed: {ex.Message}");
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            }
            await hub.DisposeAsync();
        }

        /// <summary>Builds one AI-style inference frame in source-frame pixels.</summary>
        private object BuildFrame(CameraSim cam, double dt)
        {
            var fire = Fire(cam.Zone);
            double intensity; (double X, double Y) center; bool burning; string level;
            lock (_gate) { intensity = fire.Intensity; center = fire.Center; burning = fire.Burning; level = fire.Level; }

            int w = cam.Width, h = cam.Height;
            var detections = new List<object>();
            var zones = new List<object>();
            var points = new List<double[]>();
            var danger = 0;

            if (burning)
            {
                var size = 0.06 + intensity * 0.28;
                double fw = size * w, fh = size * h * 1.2;
                detections.Add(new
                {
                    label = "fire", category = "Fire", confidence = Math.Round(0.7 + intensity * 0.29, 2),
                    bbox = new[] { center.X * w - fw / 2, center.Y * h - fh / 2, fw, fh }, trackId = $"{cam.Id}-F",
                });
                if (intensity > 0.25)
                {
                    double sw = fw * 1.6, sh = fh * 0.9;
                    detections.Add(new
                    {
                        label = "smoke", category = "Smoke", confidence = Math.Round(0.6 + intensity * 0.3, 2),
                        bbox = new[] { Math.Max(0, center.X * w - sw / 2), Math.Max(0, center.Y * h - fh / 2 - sh), sw, sh },
                        trackId = $"{cam.Id}-S",
                    });
                }
                var radius = size * 1.4;
                zones.Add(new
                {
                    label = "danger zone", level = intensity > 0.6 ? "Critical" : "Danger",
                    polygon = Enumerable.Range(0, 10).Select(i => i * Math.PI / 5)
                        .Select(a => new[] { (center.X + radius * Math.Cos(a)) * w, (center.Y + radius * 0.8 * Math.Sin(a)) * h }).ToList(),
                });
            }

            foreach (var walker in cam.Walkers)
            {
                var dx = walker.X - center.X; var dy = walker.Y - center.Y;
                var atRisk = burning && Math.Sqrt(dx * dx + dy * dy) < 0.12 + intensity * 0.3;
                walker.Step(dt, atRisk ? center : null, _rng);
                if (atRisk) danger++;
                double bw = 0.045 * w, bh = 0.2 * h;
                detections.Add(new
                {
                    label = "person", category = "Human", confidence = Math.Round(0.82 + _rng.NextDouble() * 0.16, 2),
                    bbox = new[] { walker.X * w - bw / 2, walker.Y * h - bh, bw, bh }, trackId = walker.TrackId, atRisk,
                });
                points.Add([walker.X * w, walker.Y * h]);
            }

            object? heatmap = null;
            if (burning)
            {
                var spread = 0.05 + intensity * 0.2;
                var values = new double[HeatRows * HeatCols];
                for (var r = 0; r < HeatRows; r++)
                for (var c = 0; c < HeatCols; c++)
                {
                    double x = (c + 0.5) / HeatCols, y = (r + 0.5) / HeatRows;
                    var d2 = Math.Pow(x - center.X, 2) + Math.Pow((y - center.Y) * 1.3, 2);
                    values[r * HeatCols + c] = Math.Round(Math.Min(1, intensity * 1.1 * Math.Exp(-d2 / (2 * spread * spread))), 3);
                }
                heatmap = new { rows = HeatRows, cols = HeatCols, values };
            }

            return new
            {
                cameraId = cam.Id, timestamp = DateTime.UtcNow, frameWidth = w, frameHeight = h, normalized = false,
                detections, dangerZones = zones, heatmap, humanCoordinates = points,
                fireDetected = burning, fireSeverity = burning ? level : null,
                humanCount = cam.Walkers.Count, dangerCount = danger,
            };
        }

        private double Gaussian()
        {
            var u1 = 1.0 - _rng.NextDouble();
            var u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }
    }

    public static class Log
    {
        private static void Write(string level, ConsoleColor color, string message)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {level,-5} {message}");
            Console.ForegroundColor = old;
        }
        public static void Info(string m) => Write("INFO", ConsoleColor.Gray, m);
        public static void Warn(string m) => Write("WARN", ConsoleColor.Yellow, m);
        public static void Error(string m) => Write("ERROR", ConsoleColor.Red, m);
    }
}
