using Sentinel.Api.Models;
using Sentinel.Api.Services.Notifications;

namespace Sentinel.Api.Interfaces;

/// <summary>Queues incidents for background delivery (never blocks the caller on Twilio/SMTP).</summary>
public interface INotificationDispatcher
{
    /// <summary>Returns false when suppressed by the cooldown.</summary>
    bool Enqueue(IncidentAlert incident, bool force = false);
}

/// <summary>One delivery channel: SMS, email, web push…  Add a new provider by implementing this.</summary>
public interface INotificationChannel
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<ChannelResult> SendAsync(AlertMessage message, CancellationToken ct);
}
