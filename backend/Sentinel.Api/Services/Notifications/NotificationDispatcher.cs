using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.DTOs;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;

namespace Sentinel.Api.Services.Notifications;

public record NotificationJob(int IncidentId, bool Force);

/// <summary>
/// Singleton queue in front of the notification channels. Enqueue is instant; the
/// <see cref="NotificationWorker"/> does the slow Twilio/SMTP/Push calls in the background.
/// A cooldown per (source, zone, severity) stops the same emergency from spamming responders.
/// </summary>
public class NotificationDispatcher(IMemoryCache cache, IOptions<AlertOptions> options, ILogger<NotificationDispatcher> logger)
    : INotificationDispatcher
{
    private readonly Channel<NotificationJob> _queue = Channel.CreateUnbounded<NotificationJob>();
    private readonly object _gate = new();

    public ChannelReader<NotificationJob> Reader => _queue.Reader;

    public bool Enqueue(IncidentAlert incident, bool force = false)
    {
        if (!force)
        {
            var key = $"notify:{incident.Source}:{incident.Zone}:{incident.Severity}";
            lock (_gate)
            {
                if (cache.TryGetValue(key, out _))
                {
                    logger.LogInformation("Notification for incident #{Id} suppressed by cooldown", incident.Id);
                    return false;
                }
                cache.Set(key, incident.Id, TimeSpan.FromSeconds(options.Value.CooldownSeconds));
            }
        }
        return _queue.Writer.TryWrite(new NotificationJob(incident.Id, force));
    }
}

/// <summary>Background service that drains the queue and calls every configured channel.</summary>
public class NotificationWorker(
    NotificationDispatcher dispatcher,
    IServiceScopeFactory scopes,
    IOptions<AlertOptions> options,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in dispatcher.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DeliverAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification delivery for incident #{Id} crashed", job.IncidentId);
            }
        }
    }

    private async Task DeliverAsync(NotificationJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var realtime = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();

        var incident = await db.IncidentAlerts.AsNoTracking().Include(i => i.Camera)
            .FirstOrDefaultAsync(i => i.Id == job.IncidentId, ct);
        if (incident is null) return;

        var message = AlertMessage.From(incident, options.Value.SiteName, options.Value.DashboardUrl);
        foreach (var channel in scope.ServiceProvider.GetServices<INotificationChannel>())
        {
            ChannelResult result;
            if (!channel.IsConfigured)
            {
                result = ChannelResult.NotConfigured();
            }
            else
            {
                try
                {
                    result = await channel.SendAsync(message, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Channel} notification for incident #{Id} failed", channel.Name, incident.Id);
                    result = new ChannelResult(false, 0, 0, ex.Message.Length > 300 ? ex.Message[..300] : ex.Message);
                }
            }

            logger.LogInformation("{Channel} for incident #{Id}: ok={Ok} delivered={Delivered} failed={Failed} {Detail}",
                channel.Name, incident.Id, result.Ok, result.Delivered, result.Failed, result.Detail);
            await realtime.NotificationAsync(new NotificationResultDto(
                incident.Id, channel.Name, result.Ok, result.Skipped, result.Delivered, result.Failed, result.Detail, DateTime.UtcNow));
        }
    }
}
