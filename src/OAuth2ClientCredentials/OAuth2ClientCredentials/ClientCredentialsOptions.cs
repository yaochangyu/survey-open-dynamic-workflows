using System.ComponentModel.DataAnnotations;

namespace OAuth2ClientCredentials;

public sealed class ClientCredentialsOptions
{
    [Required]
    public Uri TokenEndpoint { get; set; } = default!;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    public string? Scope { get; set; }

    public ClientAuthStyle AuthStyle { get; set; } = ClientAuthStyle.Basic;

    public TimeSpan BufferTime { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan FallbackLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);
}
