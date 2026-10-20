using System.Net;

namespace MailCleaner.Services;

public sealed class AuthenticationService
{
    public EwsTransport Create(NetworkCredential? explicitCredential = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        // No browser profile or old app credential access. Never disable TLS validation.
        var cache = new CredentialCache();
        foreach (string scheme in new[] { "Negotiate", "NTLM" })
            cache.Add(EwsTransport.Endpoint, scheme, explicitCredential ?? CredentialCache.DefaultNetworkCredentials);
        handler.Credentials = cache;
        return new EwsTransport(new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) });
    }
}
