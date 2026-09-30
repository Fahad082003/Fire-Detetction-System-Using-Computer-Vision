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
}
