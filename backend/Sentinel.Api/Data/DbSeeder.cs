using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Models;

namespace Sentinel.Api.Data;

/// <summary>Creates the first admin account and (optionally) demo cameras, sensors and sprinklers.</summary>
public class DbSeeder(SentinelDbContext db, IPasswordHasher<AppUser> hasher, IOptions<SeedOptions> seed, ILogger<DbSeeder> logger)
{
    public const double SmokeThresholdPpm = 300;
    public const double TemperatureThresholdC = 57; // standard glass-bulb sprinkler activation temperature

    /// <summary>Runs at startup. Skips (with a clear log message) if the database has not been migrated yet.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            if (!await db.Database.CanConnectAsync(ct))
            {
                logger.LogError("Cannot connect to SQL Server. Check ConnectionStrings__Sentinel and that SQL Server is running.");
                return;
            }
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                logger.LogWarning("Database has {Count} pending migration(s). Run: dotnet ef database update --project Sentinel.Api", pending.Count);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database check failed. Is SQL Server running and the connection string correct?");
            return;
        }

        await SeedAsync(ct);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await EnsureAdminAsync(ct);
        if (seed.Value.DemoData) await EnsureDemoDevicesAsync(ct);
    }

    private async Task EnsureAdminAsync(CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var password = seed.Value.AdminPassword;
        var generated = string.IsNullOrWhiteSpace(password);
        if (generated) password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));

        var admin = new AppUser
        {
            Email = seed.Value.AdminEmail.Trim().ToLowerInvariant(),
            DisplayName = "Administrator",
            Role = UserRole.Admin,
        };
        admin.PasswordHash = hasher.HashPassword(admin, password!);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        if (generated)
            logger.LogWarning("Created admin {Email} with GENERATED password: {Password}  (set Seed__AdminPassword to choose your own; shown only once)", admin.Email, password);
        else
            logger.LogInformation("Created admin account {Email}", admin.Email);
    }

    // Zones are named "Floor - Area" so an alert says where to go; cameras, sensors, sprinklers and plan danger zones must use the same name.
    private async Task EnsureDemoDevicesAsync(CancellationToken ct)
    {
        if (await db.CameraFeeds.AnyAsync(ct) || await db.SensorNodes.AnyAsync(ct) || await db.SmartSprinklers.AnyAsync(ct)) return;

        db.CameraFeeds.AddRange(
            new CameraFeed { Id = "cam-lobby", Name = "Main Lobby", FeedType = FeedType.CCTV, LocationZone = "Ground Floor - Lobby" },
            new CameraFeed { Id = "cam-warehouse", Name = "Warehouse Floor", FeedType = FeedType.CCTV, LocationZone = "Ground Floor - Warehouse" },
            new CameraFeed { Id = "cam-server", Name = "Server Hall", FeedType = FeedType.CCTV, LocationZone = "First Floor - Server Hall" },
            new CameraFeed { Id = "drone-01", Name = "Perimeter Drone 01", FeedType = FeedType.Drone, LocationZone = "Ground Floor - Warehouse" });

        foreach (var (zone, suffix) in new[] { ("Ground Floor - Lobby", "a"), ("Ground Floor - Warehouse", "b"), ("First Floor - Server Hall", "c") })
        {
            db.SensorNodes.AddRange(
                new SensorNode { Id = $"smoke-{suffix}1", Type = SensorType.Smoke, Threshold = SmokeThresholdPpm, CurrentValue = 18, LocationZone = zone },
                new SensorNode { Id = $"temp-{suffix}1", Type = SensorType.Temperature, Threshold = TemperatureThresholdC, CurrentValue = 22, LocationZone = zone });
        }

        db.SmartSprinklers.AddRange(
            new SmartSprinkler { Id = "spr-a1", Zone = "Ground Floor - Lobby" },
            new SmartSprinkler { Id = "spr-a2", Zone = "Ground Floor - Lobby" },
            new SmartSprinkler { Id = "spr-b1", Zone = "Ground Floor - Warehouse" },
            new SmartSprinkler { Id = "spr-b2", Zone = "Ground Floor - Warehouse" },
            new SmartSprinkler { Id = "spr-c1", Zone = "First Floor - Server Hall" });

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded demo data: 4 cameras, 6 sensors, 5 sprinklers");
    }
}
