using Microsoft.EntityFrameworkCore;
using Sentinel.Api.Models;

namespace Sentinel.Api.Data;

public class SentinelDbContext(DbContextOptions<SentinelDbContext> options) : DbContext(options)
{
    public DbSet<CameraFeed> CameraFeeds => Set<CameraFeed>();
    public DbSet<SensorNode> SensorNodes => Set<SensorNode>();
    public DbSet<SensorReading> SensorReadings => Set<SensorReading>();
    public DbSet<SmartSprinkler> SmartSprinklers => Set<SmartSprinkler>();
    public DbSet<IncidentAlert> IncidentAlerts => Set<IncidentAlert>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<WebPushSubscription> PushSubscriptions => Set<WebPushSubscription>();
    public DbSet<IncidentLog> IncidentLogs => Set<IncidentLog>();
    public DbSet<FloorPlan> FloorPlans => Set<FloorPlan>();
    public DbSet<FloorPlanElement> FloorPlanElements => Set<FloorPlanElement>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Store every enum as readable text ("Critical" instead of 3) in SQL Server.
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(16);
        // All times are stored in UTC. SQL Server's datetime2 forgets the "Kind", so mark values read back as UTC;
        // otherwise JSON would omit the trailing "Z" and browsers would treat them as local time.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CameraFeed>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.StreamUrl).HasMaxLength(500);
            e.Property(x => x.LocationZone).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.LocationZone);
        });

        b.Entity<SensorNode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.LocationZone).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.LocationZone);
            e.Ignore(x => x.Unit);
            e.ToTable(t => t.HasCheckConstraint("CK_SensorNodes_Threshold", "[Threshold] > 0"));
        });

        b.Entity<SensorReading>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.SensorId).HasMaxLength(64);
            e.HasOne(x => x.Sensor).WithMany(s => s.Readings).HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SensorId, x.RecordedAt });
            e.HasIndex(x => x.RecordedAt);
        });

        b.Entity<SmartSprinkler>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Zone).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Zone);
        });

        b.Entity<IncidentAlert>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Zone).HasMaxLength(64).IsRequired();
            e.Property(x => x.Message).HasMaxLength(500);
            e.Property(x => x.CameraId).HasMaxLength(64);
            e.Property(x => x.SensorId).HasMaxLength(64);
            e.HasOne(x => x.Camera).WithMany(c => c.Incidents).HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Sensor).WithMany(s => s.Incidents).HasForeignKey(x => x.SensorId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.IsResolved, x.Timestamp });
            e.HasIndex(x => x.Zone);
        });

        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<IncidentLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Zone).HasMaxLength(120);
            e.Property(x => x.Details).HasMaxLength(500);
            e.Property(x => x.PersonTrackId).HasMaxLength(48);
            e.Property(x => x.DangerZone).HasMaxLength(48);
            e.HasOne(x => x.Incident).WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.IncidentId, x.Timestamp });
            e.HasIndex(x => new { x.Type, x.Timestamp });
        });

        b.Entity<FloorPlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Building).HasMaxLength(120);
            e.Property(x => x.ImageFileName).HasMaxLength(64);
        });

        b.Entity<FloorPlanElement>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Zone).HasMaxLength(64);
            e.HasOne(x => x.FloorPlan).WithMany(p => p.Elements).HasForeignKey(x => x.FloorPlanId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WebPushSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Endpoint).HasMaxLength(500).IsRequired();
            e.Property(x => x.P256dh).HasMaxLength(255).IsRequired();
            e.Property(x => x.Auth).HasMaxLength(255).IsRequired();
            e.Property(x => x.UserAgent).HasMaxLength(255);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public class UtcDateTimeConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
