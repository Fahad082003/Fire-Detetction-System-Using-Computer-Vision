using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;

namespace Sentinel.Api.Tests;

/// <summary>Runs the real API in-memory with an SQLite in-memory database instead of SQL Server.</summary>
public class SentinelApiFactory : WebApplicationFactory<Program>
{
    public const string DeviceKey = "test-device-key";
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Test@12345";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private string? _token;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Device:ApiKey", DeviceKey);
        builder.UseSetting("Jwt:SigningKey", "unit-test-signing-key-0123456789-abcdefghijklmnop");
        builder.UseSetting("Seed:DemoData", "true");
        builder.UseSetting("Seed:AdminEmail", AdminEmail);
        builder.UseSetting("Seed:AdminPassword", AdminPassword);
        builder.UseSetting("FloorPlans:ImageDirectory", Path.Combine(Path.GetTempPath(), "sentinel-tests", Guid.NewGuid().ToString("N")));

        builder.ConfigureServices(services =>
        {
            // One shared in-memory SQLite connection is not thread-safe: keep tests single-threaded against it
            // by disabling the background notification worker (queued jobs simply stay in memory).
            services.RemoveAll<IHostedService>();
            services.RemoveAll<DbContextOptions<SentinelDbContext>>();
            _connection.Open();
            services.AddDbContext<SentinelDbContext>(o => o.UseSqlite(_connection));

            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
            db.Database.EnsureCreated();
            scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync().GetAwaiter().GetResult();
        });
    }

    public HttpClient DeviceClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", DeviceKey);
        return client;
    }

    public async Task<string> LoginAsync()
    {
        var resp = await CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AdminEmail, password = AdminPassword });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<LoginResponse>(TestJson.Options))!.Token;
    }

    public async Task<HttpClient> OperatorClientAsync()
    {
        var client = CreateClient();
        // One login per factory: the login endpoint is rate limited.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token ??= await LoginAsync());
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

internal static class TestJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
