using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OAuth2ClientCredentials;

public sealed class ClientCredentialsTokenService(
    IHttpClientFactory httpClientFactory,
    IOptions<ClientCredentialsOptions> options,
    TimeProvider timeProvider) : IClientCredentialsTokenService, IDisposable
{
    public const string TokenHttpClientName = "oauth2-client-credentials-token";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ClientCredentialsOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedToken? _cached;

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _cached);
        if (IsFresh(cached))
        {
            return cached!.AccessToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (IsFresh(cached))
            {
                return cached!.AccessToken;
            }

            var fresh = await RequestTokenWithRetryAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _cached, fresh);
            return fresh.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(string rejectedToken)
    {
        var cached = Volatile.Read(ref _cached);
        if (cached is not null && cached.AccessToken == rejectedToken)
        {
            Interlocked.CompareExchange(ref _cached, null, cached);
        }
    }

    public void Dispose() => _gate.Dispose();

    private bool IsFresh(CachedToken? token) =>
        token is not null && timeProvider.GetUtcNow() < token.RefreshAt;

    private async Task<CachedToken> RequestTokenWithRetryAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TokenRequestException ex) when (ex.IsTransient && attempt < _options.MaxRetryAttempts)
            {
                attempt++;
                await Task.Delay(ComputeRetryDelay(attempt), timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var multiplier = Math.Pow(2, attempt - 1);
        var ticks = (long)Math.Min(_options.RetryDelay.Ticks * multiplier, TimeSpan.FromSeconds(5).Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private async Task<CachedToken> RequestTokenAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials"
        };

        if (!string.IsNullOrWhiteSpace(_options.Scope))
        {
            form["scope"] = _options.Scope;
        }

        if (_options.AuthStyle == ClientAuthStyle.Basic)
        {
            var rawCredentials = $"{Uri.EscapeDataString(_options.ClientId)}:{Uri.EscapeDataString(_options.ClientSecret)}";
            var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawCredentials));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
        }
        else
        {
            form["client_id"] = _options.ClientId;
            form["client_secret"] = _options.ClientSecret;
        }

        request.Content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory
                .CreateClient(TokenHttpClientName)
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TokenRequestException(HttpStatusCode.ServiceUnavailable, null, ex.Message, isTransient: true, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tokenResponse = DeserializeTokenResponse(body);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateTokenRequestException(response.StatusCode, tokenResponse);
            }

            if (string.IsNullOrWhiteSpace(tokenResponse?.AccessToken))
            {
                throw new TokenRequestException(response.StatusCode, "invalid_response", "missing access_token", isTransient: false);
            }

            var lifetime = ParseLifetime(tokenResponse.ExpiresIn) ?? _options.FallbackLifetime;
            var buffer = TimeSpan.FromTicks(Math.Min(_options.BufferTime.Ticks, lifetime.Ticks / 2));
            var refreshAt = timeProvider.GetUtcNow() + lifetime - buffer;

            return new CachedToken(tokenResponse.AccessToken, refreshAt);
        }
    }

    private static TokenResponse? DeserializeTokenResponse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<TokenResponse>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TokenRequestException CreateTokenRequestException(HttpStatusCode statusCode, TokenResponse? tokenResponse)
    {
        var isTransient = (int)statusCode >= 500
            || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

        return new TokenRequestException(
            statusCode,
            tokenResponse?.Error,
            tokenResponse?.ErrorDescription,
            isTransient);
    }

    private static TimeSpan? ParseLifetime(JsonElement expiresIn) =>
        expiresIn.ValueKind switch
        {
            JsonValueKind.Number when expiresIn.TryGetInt64(out var seconds) => TimeSpan.FromSeconds(seconds),
            JsonValueKind.String when long.TryParse(expiresIn.GetString(), out var seconds) => TimeSpan.FromSeconds(seconds),
            _ => null
        };

    private sealed record CachedToken(string AccessToken, DateTimeOffset RefreshAt);
}
