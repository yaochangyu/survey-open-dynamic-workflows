using System.Net;
using Microsoft.Extensions.Options;

namespace OAuth2ClientCredentials.Tests;

public sealed class ClientCredentialsTokenServiceTests
{
    [Fact]
    public async Task GetTokenAsync_RequestsTokenEndpointAndReturnsToken()
    {
        using var tokenHandler = new QueueHttpMessageHandler(_ =>
            JsonResponse("""{"access_token":"token-1","token_type":"Bearer","expires_in":3600}"""));
        var service = CreateTokenService(tokenHandler);

        var token = await service.GetTokenAsync();

        Assert.Equal("token-1", token);
        Assert.Equal(1, tokenHandler.CallCount);
        Assert.Equal(HttpMethod.Post, tokenHandler.Requests[0].Method);
        Assert.Equal("/token", tokenHandler.Requests[0].RequestUri?.AbsolutePath);
        Assert.Equal("Basic", tokenHandler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Contains("grant_type=client_credentials", await tokenHandler.Requests[0].Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetTokenAsync_WhenTokenIsFresh_UsesCache()
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
        using var tokenHandler = new QueueHttpMessageHandler(_ =>
            JsonResponse("""{"access_token":"cached-token","token_type":"Bearer","expires_in":120}"""));
        var service = CreateTokenService(tokenHandler, timeProvider: timeProvider);

        var first = await service.GetTokenAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var second = await service.GetTokenAsync();

        Assert.Equal("cached-token", first);
        Assert.Equal("cached-token", second);
        Assert.Equal(1, tokenHandler.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_WhenConcurrent_RequestsTokenEndpointOnce()
    {
        using var tokenHandler = new QueueHttpMessageHandler(async _ =>
        {
            await Task.Delay(50);
            return JsonResponse("""{"access_token":"shared-token","token_type":"Bearer","expires_in":3600}""");
        });
        var service = CreateTokenService(tokenHandler);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetTokenAsync().AsTask()));

        Assert.All(tokens, token => Assert.Equal("shared-token", token));
        Assert.Equal(1, tokenHandler.CallCount);
    }

    [Fact]
    public async Task SendAsync_WhenApiReturnsUnauthorized_InvalidatesTokenAndRetriesOnce()
    {
        var tokenSequence = 0;
        using var tokenHandler = new QueueHttpMessageHandler(_ =>
        {
            var call = Interlocked.Increment(ref tokenSequence);
            return JsonResponse($$"""{"access_token":"token-{{call}}","token_type":"Bearer","expires_in":3600}""");
        });
        var tokenService = CreateTokenService(tokenHandler);
        using var apiHandler = new QueueHttpMessageHandler(request =>
        {
            var token = request.Headers.Authorization?.Parameter;
            return token == "token-1"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        using var bearerHandler = new BearerTokenDelegatingHandler(tokenService)
        {
            InnerHandler = apiHandler
        };
        using var httpClient = new HttpClient(bearerHandler);

        var response = await httpClient.GetAsync("https://api.example.test/resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, tokenHandler.CallCount);
        Assert.Equal(2, apiHandler.CallCount);
        Assert.Equal("token-1", apiHandler.Requests[0].Headers.Authorization?.Parameter);
        Assert.Equal("token-2", apiHandler.Requests[1].Headers.Authorization?.Parameter);
    }

    private static ClientCredentialsTokenService CreateTokenService(
        HttpMessageHandler tokenHandler,
        MutableTimeProvider? timeProvider = null) =>
        new(
            new StubHttpClientFactory(tokenHandler),
            Options.Create(new ClientCredentialsOptions
            {
                TokenEndpoint = new Uri("https://idp.example.test/token"),
                ClientId = "client-id",
                ClientSecret = "client-secret",
                RetryDelay = TimeSpan.Zero
            }),
            timeProvider ?? new MutableTimeProvider(DateTimeOffset.Parse("2026-10-11T00:00:00Z")));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }

    private sealed class QueueHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        private int _callCount;

        public QueueHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            : this(request => Task.FromResult(responseFactory(request)))
        {
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Requests.Add(await CloneRequestAsync(request, cancellationToken));
            return await responseFactory(request).ConfigureAwait(false);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);
            foreach (var header in source.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (source.Content is not null)
            {
                var body = await source.Content.ReadAsByteArrayAsync(cancellationToken);
                clone.Content = new ByteArrayContent(body);
                foreach (var header in source.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
