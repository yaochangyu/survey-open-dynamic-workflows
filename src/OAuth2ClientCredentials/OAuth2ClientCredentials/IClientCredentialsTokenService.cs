namespace OAuth2ClientCredentials;

public interface IClientCredentialsTokenService
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);

    void Invalidate(string rejectedToken);
}
