using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OAuth2ClientCredentials;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClientCredentialsTokenManagement(
        this IServiceCollection services,
        string configurationSectionName)
    {
        services.AddOptions<ClientCredentialsOptions>()
            .BindConfiguration(configurationSectionName)
            .ValidateDataAnnotations()
            .Validate(ValidateOptions, "TokenEndpoint must use HTTPS, and timing/retry values must be positive.")
            .ValidateOnStart();

        return AddCoreServices(services);
    }

    public static IServiceCollection AddClientCredentialsTokenManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ClientCredentialsOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .Validate(ValidateOptions, "TokenEndpoint must use HTTPS, and timing/retry values must be positive.")
            .ValidateOnStart();

        return AddCoreServices(services);
    }

    public static IServiceCollection AddClientCredentialsTokenManagement(
        this IServiceCollection services,
        Action<ClientCredentialsOptions> configure)
    {
        services.AddOptions<ClientCredentialsOptions>()
            .Configure(configure)
            .ValidateDataAnnotations()
            .Validate(ValidateOptions, "TokenEndpoint must use HTTPS, and timing/retry values must be positive.")
            .ValidateOnStart();

        return AddCoreServices(services);
    }

    private static IServiceCollection AddCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IClientCredentialsTokenService, ClientCredentialsTokenService>();
        services.TryAddTransient<BearerTokenDelegatingHandler>();
        services.AddHttpClient(ClientCredentialsTokenService.TokenHttpClientName);

        return services;
    }

    private static bool ValidateOptions(ClientCredentialsOptions options) =>
        options.TokenEndpoint is { Scheme: "https" }
        && options.BufferTime >= TimeSpan.Zero
        && options.FallbackLifetime > TimeSpan.Zero
        && options.MaxRetryAttempts >= 0
        && options.RetryDelay >= TimeSpan.Zero;
}
