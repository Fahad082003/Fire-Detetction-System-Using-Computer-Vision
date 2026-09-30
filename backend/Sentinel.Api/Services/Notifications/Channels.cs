using System.Net;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using Sentinel.Api.Configuration;
using Sentinel.Api.Data;
using Sentinel.Api.Interfaces;
using Sentinel.Api.Models;
using Twilio.Clients;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;
using WebPush;

namespace Sentinel.Api.Services.Notifications;

/// <summary>SMS through the Twilio REST API.</summary>
public class TwilioSmsChannel(IOptions<NotificationOptions> options, ILogger<TwilioSmsChannel> logger) : INotificationChannel
{
    private SmsOptions Sms => options.Value.Sms;
    public string Name => "sms";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Sms.TwilioAccountSid) && !string.IsNullOrWhiteSpace(Sms.TwilioAuthToken)
        && !string.IsNullOrWhiteSpace(Sms.FromNumber) && Sms.Recipients.SplitList().Length > 0;

    public async Task<ChannelResult> SendAsync(AlertMessage message, CancellationToken ct)
    {
        var client = new TwilioRestClient(Sms.TwilioAccountSid, Sms.TwilioAuthToken);
        var body = message.Text.Length > 1500 ? message.Text[..1500] : message.Text;
        int delivered = 0;
        var errors = new List<string>();

        foreach (var number in Sms.Recipients.SplitList())
        {
            try
            {
                await MessageResource.CreateAsync(to: new PhoneNumber(number), from: new PhoneNumber(Sms.FromNumber), body: body, client: client);
                delivered++;
            }
            catch (ApiException ex)
            {
                logger.LogWarning("Twilio SMS to {Number} failed: {Error}", number, ex.Message);
                errors.Add($"{number}: {ex.Message}");
            }
        }
        return new ChannelResult(delivered > 0, delivered, errors.Count, string.Join("; ", errors));
    }
}

/// <summary>Email through any SMTP server (MailKit). With no SMTP host, the email is written to the log ("console mode").</summary>
public class EmailChannel(IOptions<NotificationOptions> options, ILogger<EmailChannel> logger) : INotificationChannel
{
    private EmailOptions Email => options.Value.Email;
    public string Name => "email";
    public bool IsConfigured => Email.Recipients.SplitList().Length > 0;

    public async Task<ChannelResult> SendAsync(AlertMessage message, CancellationToken ct)
    {
        var recipients = Email.Recipients.SplitList();
        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(MailboxAddress.Parse(Email.From));
        foreach (var r in recipients) mime.To.Add(MailboxAddress.Parse(r));
        mime.Priority = message.Severity >= Severity.High ? MessagePriority.Urgent : MessagePriority.Normal;
        mime.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = RenderHtml(message) }.ToMessageBody();

        if (string.IsNullOrWhiteSpace(Email.SmtpHost))
        {
            logger.LogWarning("[EMAIL console mode] To: {To}\nSubject: {Subject}\n{Body}", string.Join(", ", recipients), mime.Subject, message.Text);
            return new ChannelResult(true, recipients.Length, 0, "console mode (no SMTP host configured)");
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(Email.SmtpHost, Email.SmtpPort, Email.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(Email.Username))
            await smtp.AuthenticateAsync(Email.Username, Email.Password, ct);
        await smtp.SendAsync(mime, ct);
        await smtp.DisconnectAsync(true, ct);
        return new ChannelResult(true, recipients.Length, 0, "");
    }

    private static string RenderHtml(AlertMessage m)
    {
        var color = m.Severity >= Severity.High ? "#b91c1c" : m.Severity == Severity.Medium ? "#b45309" : "#047857";
        string E(string s) => WebUtility.HtmlEncode(s);
        return $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;border:1px solid #1f2937;border-radius:10px;overflow:hidden">
              <div style="background:{color};color:#fff;padding:16px 20px">
                <div style="font-size:12px;letter-spacing:.12em;text-transform:uppercase">{E(m.SiteName)} · Incident #{m.IncidentId}</div>
                <div style="font-size:20px;font-weight:700">{m.Severity.ToString().ToUpperInvariant()} — {E(m.Headline)}</div>
              </div>
              <div style="padding:16px 20px;color:#111827">
                <p>{E(m.Message)}</p>
                <p><b>Location:</b> {E(m.Location)}<br><b>Humans in area:</b> {m.HumanCount}<br>
                   <b>Humans in danger:</b> {m.DangerCount}<br><b>Detected:</b> {m.Timestamp:yyyy-MM-dd HH:mm:ss} UTC</p>
                <a href="{E(m.DashboardUrl)}" style="background:#ef4444;color:#fff;padding:10px 16px;border-radius:8px;text-decoration:none">Open Command Center</a>
              </div>
            </div>
            """;
    }
}

/// <summary>Browser Web Push (VAPID). Expired subscriptions (404/410) are deactivated automatically.</summary>
public class WebPushChannel(IOptions<NotificationOptions> options, SentinelDbContext db, ILogger<WebPushChannel> logger) : INotificationChannel
{
    private WebPushOptions Push => options.Value.WebPush;
    public string Name => "push";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Push.PublicKey) && !string.IsNullOrWhiteSpace(Push.PrivateKey);

    public async Task<ChannelResult> SendAsync(AlertMessage message, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            title = $"{message.Severity.ToString().ToUpperInvariant()} · {message.Headline}",
            body = $"{message.Location} — humans in danger: {message.DangerCount}",
            tag = $"incident-{message.IncidentId}",
            url = message.DashboardUrl,
            requireInteraction = message.Severity >= Severity.High,
        });
        var vapid = new VapidDetails(Push.Subject, Push.PublicKey, Push.PrivateKey);
        using var client = new WebPushClient();
        int delivered = 0, failed = 0;
        var stale = new List<int>();

        foreach (var sub in await db.PushSubscriptions.Where(s => s.IsActive).ToListAsync(ct))
        {
            try
            {
                await client.SendNotificationAsync(new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth), payload, vapid, ct);
                delivered++;
            }
            catch (WebPushException ex)
            {
                failed++;
                if (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) stale.Add(sub.Id);
                else logger.LogWarning("Web push failed ({Status}): {Error}", ex.StatusCode, ex.Message);
            }
        }
        if (stale.Count > 0)
            await db.PushSubscriptions.Where(s => stale.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);

        return new ChannelResult(delivered > 0 || failed == 0, delivered, failed,
            stale.Count > 0 ? $"{stale.Count} expired subscription(s) removed" : delivered + failed == 0 ? "no subscribers" : "");
    }
}
