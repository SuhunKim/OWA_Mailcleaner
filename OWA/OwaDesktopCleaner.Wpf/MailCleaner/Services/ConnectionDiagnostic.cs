using System.Net;
using System.Net.Http;

namespace MailCleaner.Services;

// Only fixed codes and allowlisted challenge names enter the shareable report.
// Never include exception messages, header parameters, redirect URLs or response bodies.
public sealed record ConnectionDiagnostic(string Stage, string Code, string Guidance, int? HttpStatus = null, string Schemes = "None", int? NativeCode = null, string SocketCode = "None")
{
    public string Report => $"App: {typeof(ConnectionDiagnostic).Assembly.GetName().Version}\nStage: {Stage}\nCode: {Code}\nHTTP: {HttpStatus?.ToString() ?? "None"}\nAuth: {Schemes}\nNativeCode: {(NativeCode is int code ? $"0x{code:X8}" : "None")}\nSocket: {SocketCode}";
    public static ConnectionDiagnostic Network(HttpRequestException error)
    {
        var result = Classify(error);
        var cause = error.GetBaseException();
        return result with { NativeCode = cause is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : cause.HResult,
            SocketCode = cause is System.Net.Sockets.SocketException socket ? socket.SocketErrorCode.ToString() : "None" };
    }
    static ConnectionDiagnostic Classify(HttpRequestException error) => error.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => new("DNS", "NameResolutionError", "회사 메일 서버 이름을 찾지 못했습니다. DNS 또는 VPN 연결을 확인하세요."),
        HttpRequestError.ConnectionError => new("Connect", "ConnectionError", "EWS 연결을 열지 못했습니다. 네트워크·방화벽·VPN을 확인하세요."),
        HttpRequestError.SecureConnectionError => new("TLS", "SecureConnectionError", "TLS 연결에 실패했습니다. PC 시간·회사 인증서·보안 프로그램 정책을 확인하세요. 인증서 검증은 유지됩니다."),
        HttpRequestError.ProxyTunnelError => new("Proxy", "ProxyTunnelError", "프록시 터널 연결에 실패했습니다. Windows 프록시와 회사 프록시 인증 정책을 확인하세요."),
        HttpRequestError.UserAuthenticationError => new("Authentication", "UserAuthenticationError", "통합 인증 협상에 실패했습니다. 회사 계정 형식과 외부 EWS의 Negotiate/NTLM 허용 여부를 확인하세요."),
        _ => new("Transport", error.HttpRequestError.ToString(), "EWS 통신에 실패했습니다. 연결 진단 정보를 복사해 전달하세요.")
    };
    public static ConnectionDiagnostic Timeout() => new("Transport", "Timeout", "EWS 응답 시간이 초과됐습니다. 네트워크·프록시·서버 상태를 확인하세요.");
    public static ConnectionDiagnostic Response(HttpResponseMessage response, bool anonymous = false)
    {
        int status = (int)response.StatusCode;
        string[] known = ["Negotiate", "NTLM", "Basic", "Digest", "Bearer"];
        string schemes = string.Join(",", known.Where(name => response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals(name, StringComparison.OrdinalIgnoreCase))));
        if (schemes.Length == 0) schemes = "None";
        var (stage, code, guidance) = status switch
        {
            401 when anonymous => ("Authentication", "ChallengeObserved", "EWS 서버에 도달했습니다. 인증 요구를 확인했으며 로그인 성공을 뜻하지 않습니다. 회사 계정으로 연결하세요."),
            401 => ("Authentication", "Unauthorized", "EWS 인증이 거부됐습니다. 회사 계정 형식을 확인하세요. OWA 로그인 성공과 EWS 인증은 별개입니다."),
            403 => ("Access", "Forbidden", "EWS 접근이 거부됐습니다. 외부 EWS·계정 접근 정책을 회사 관리자에게 확인하세요."),
            407 => ("Proxy", "ProxyAuthenticationRequired", "프록시 인증이 필요합니다. Windows 프록시 및 회사 인증 정책을 확인하세요."),
            >= 300 and < 400 => ("Authentication", "RedirectBlocked", "서버가 다른 주소로 이동을 요구했습니다. 자동 이동은 차단했습니다. 외부 EWS 주소·브라우저 로그인 정책 확인이 필요합니다."),
            >= 200 and < 300 when anonymous => ("HTTP", "EndpointReachable", "서버에 도달했습니다. 익명 진단만으로 EWS 인증·메일 조회 성공을 확인할 수는 없습니다."),
            _ => ("HTTP", "HttpFailure", "EWS HTTP 오류입니다. 상태 코드와 회사 서버 정책을 확인하세요.")
        };
        return new(stage, code, guidance, status, schemes);
    }
}

public sealed class EwsConnectionException(ConnectionDiagnostic diagnostic, bool authentication = false)
    : EwsException(diagnostic.Guidance, authentication)
{
    public ConnectionDiagnostic Diagnostic { get; } = diagnostic;
}

public static class ConnectionProbe
{
    public static async Task<ConnectionDiagnostic> RunAsync(CancellationToken cancellation)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return await RunAsync(http, cancellation);
    }
    // Anonymous SOAP request: same verb/endpoint as the app, no user credentials or mailbox changes.
    public static async Task<ConnectionDiagnostic> RunAsync(HttpClient http, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EwsTransport.Endpoint);
        request.Headers.Add("SOAPAction", "\"http://schemas.microsoft.com/exchange/services/2006/messages/GetFolder\"");
        request.Content = new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/' xmlns:m='http://schemas.microsoft.com/exchange/services/2006/messages' xmlns:t='http://schemas.microsoft.com/exchange/services/2006/types'><s:Body><m:GetFolder><m:FolderShape><t:BaseShape>IdOnly</t:BaseShape></m:FolderShape><m:FolderIds><t:DistinguishedFolderId Id='inbox'/></m:FolderIds></m:GetFolder></s:Body></s:Envelope>", System.Text.Encoding.UTF8, "text/xml");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            return ConnectionDiagnostic.Response(response, anonymous: true);
        }
        catch (HttpRequestException ex) { return ConnectionDiagnostic.Network(ex); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return ConnectionDiagnostic.Timeout(); }
    }
}
