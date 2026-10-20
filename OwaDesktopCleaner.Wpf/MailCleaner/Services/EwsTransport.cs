using System.IO;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MailCleaner.Services;

public class EwsException(string code, bool authentication = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Authentication { get; } = authentication;
}
public sealed class MutationUnknownException() : Exception("변경 응답 미확인 · 자동 재시도 안 함");

public sealed class EwsTransport(HttpClient http) : IDisposable
{
    public static readonly Uri Endpoint = new("https://mail.apsfamily.com/EWS/Exchange.asmx");
    public static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
    public static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    public static readonly XNamespace S = "http://schemas.xmlsoap.org/soap/envelope/";
    static readonly HashSet<string> Reads = ["GetFolder", "FindFolder", "FindItem", "GetItem"];
    static readonly HashSet<string> Writes = ["UpdateItem", "MoveItem", "DeleteItem", "DeleteAttachment"];
    public bool Authenticated { get; private set; }
    public bool AllowChanges { get; set; }
    public event Action? AuthenticationLost;
    public void MarkAuthenticated() => Authenticated = true;
    void Expired()
    {
        Authenticated = false; AllowChanges = false; AuthenticationLost?.Invoke();
    }
    public async Task<XElement[]> CallAsync(XElement operation, CancellationToken cancellation)
    {
        string name = operation.Name.LocalName;
        bool write = Writes.Contains(name);
        if (operation.Name.Namespace != M || (!Reads.Contains(name) && !write)) throw new EwsException("요청 차단");
        if (write && (!Authenticated || !AllowChanges)) throw new EwsException("변경 권한 또는 인증 필요");
        cancellation.ThrowIfCancellationRequested();
        var envelope = new XElement(S + "Envelope", new XAttribute(XNamespace.Xmlns + "m", M), new XAttribute(XNamespace.Xmlns + "t", T),
            new XElement(S + "Header", new XElement(T + "RequestServerVersion", new XAttribute("Version", "Exchange2013"))), new XElement(S + "Body", operation));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("SOAPAction", $"\"{M}/{name}\"");
        request.Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml");
        bool sent = false;
        try
        {
            sent = true;
            using var response = await http.SendAsync(request, cancellation);
            int status = (int)response.StatusCode;
            if (status is 401 or 403 or 440 || status is >= 300 and < 400)
            {
                Expired(); throw new EwsException("인증 만료 또는 거부 · 다시 연결하세요", true);
            }
            if (!response.IsSuccessStatusCode) throw new EwsException($"HTTP {status}");
            if (response.Content.Headers.ContentType?.MediaType is not ("text/xml" or "application/soap+xml"))
            {
                Expired(); throw new EwsException("SOAP 대신 로그인 화면 또는 비정상 응답", true);
            }
            using var input = new StringReader(await response.Content.ReadAsStringAsync(cancellation));
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
            var document = XDocument.Load(reader);
            var body = document.Root?.Name == S + "Envelope" ? document.Root.Element(S + "Body") : null;
            var messages = body?.Element(M + (name + "Response"))?.Element(M + "ResponseMessages")?.Elements().ToArray();
            if (messages is not { Length: > 0 } || messages.Any(x => x.Name != M + (name + "ResponseMessage"))) throw new EwsException("응답 구조 미확인");
            if (messages.Any(x => (string?)x.Element(M + "ResponseCode") is "ErrorAccessDenied" or "ErrorInvalidClientAccessToken" or "ErrorInvalidUserSid"))
            {
                Expired(); throw new EwsException("SOAP 인증 거부", true);
            }
            return messages;
        }
        catch (Exception ex) when (write && sent && ex is not MutationUnknownException)
        {
            // Even an HTTP/authentication error after submission does not prove a mutation's outcome.
            throw new MutationUnknownException();
        }
    }
    public static void RequireSuccess(XElement message)
    {
        if ((string?)message.Attribute("ResponseClass") != "Success" || (string?)message.Element(M + "ResponseCode") != "NoError")
        {
            string code = (string?)message.Element(M + "ResponseCode") ?? "응답 누락";
            // Only EWS code, never server MessageText or headers.
            throw new EwsException(code.StartsWith("Error", StringComparison.Ordinal) && code.All(char.IsAsciiLetterOrDigit) ? code : "SOAP 오류");
        }
    }
    public void Dispose() => http.Dispose();
}
