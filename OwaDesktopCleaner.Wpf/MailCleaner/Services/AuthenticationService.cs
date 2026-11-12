using System.Net;

namespace MailCleaner.Services;

public sealed class AuthenticationService
{
    public static HttpClientHandler CreateHandler(NetworkCredential? explicitCredential = null)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        // No browser profile or old app credential access. Never disable TLS validation.
        var cache = new CredentialCache();
        foreach (string scheme in new[] { "Negotiate", "NTLM" })
            cache.Add(EwsTransport.Endpoint, scheme, explicitCredential ?? CredentialCache.DefaultNetworkCredentials);
        handler.Credentials = cache;
        return handler;
    }
    public EwsTransport Create(NetworkCredential? explicitCredential = null) =>
        new(new HttpClient(CreateHandler(explicitCredential)) { Timeout = TimeSpan.FromMinutes(3) });

    public static NetworkCredential ExplicitCredential(string user, System.Security.SecureString password)
    {
        user = user.Trim();
        int slash = user.IndexOf('\\');
        return slash > 0 && slash < user.Length - 1
            ? new NetworkCredential(user[(slash + 1)..], password, user[..slash])
            : new NetworkCredential(user, password);
    }
}
