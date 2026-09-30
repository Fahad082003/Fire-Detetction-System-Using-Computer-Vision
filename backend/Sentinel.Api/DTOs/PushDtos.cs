using System.ComponentModel.DataAnnotations;

namespace Sentinel.Api.DTOs;

public class PushSubscriptionRequest
{
    [Required, Url, MaxLength(500)]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public PushKeysDto Keys { get; set; } = new();
}

public class PushKeysDto
{
    [Required, MaxLength(255)]
    public string P256dh { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string Auth { get; set; } = string.Empty;
}

public class PushUnsubscribeRequest
{
    [Required]
    public string Endpoint { get; set; } = string.Empty;
}

public record VapidKeyResponse(string? PublicKey);
