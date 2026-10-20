using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OwaDesktopCleaner.Wpf.ConnectionCheck;

// Read-only feasibility check. No cookies, profile access, credential storage or mutations.
internal sealed class EwsReadClient(HttpClient http)
{
    internal static readonly Uri Endpoint = new("https://mail.apsfamily.com/EWS/Exchange.asmx");
    internal static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
    internal static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    internal static readonly XNamespace S = "http://schemas.xmlsoap.org/soap/envelope/";

    internal static XElement GetFolder() => new(M + "GetFolder",
        new XElement(M + "FolderShape", new XElement(T + "BaseShape", "IdOnly")),
        new XElement(M + "FolderIds", new XElement(T + "DistinguishedFolderId", new XAttribute("Id", "inbox"))));

    internal static XElement FindFolder() => new(M + "FindFolder", new XAttribute("Traversal", "Deep"),
        new XElement(M + "FolderShape", new XElement(T + "BaseShape", "IdOnly")),
        new XElement(M + "IndexedPageFolderView", new XAttribute("MaxEntriesReturned", 5),
            new XAttribute("Offset", 0), new XAttribute("BasePoint", "Beginning")),
        new XElement(M + "ParentFolderIds", new XElement(T + "DistinguishedFolderId", new XAttribute("Id", "msgfolderroot"))));

    internal static XElement FindItem(string folderId, string keyword) => new(M + "FindItem", new XAttribute("Traversal", "Shallow"),
        new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly")),
        new XElement(M + "IndexedPageItemView", new XAttribute("MaxEntriesReturned", 5),
            new XAttribute("Offset", 0), new XAttribute("BasePoint", "Beginning")),
        string.IsNullOrWhiteSpace(keyword) ? null : new XElement(M + "Restriction",
            new XElement(T + "Contains", new XAttribute("ContainmentMode", "Substring"),
                new XAttribute("ContainmentComparison", "IgnoreCase"),
                new XElement(T + "FieldURI", new XAttribute("FieldURI", "item:Subject")),
                new XElement(T + "Constant", new XAttribute("Value", keyword)))),
        new XElement(M + "ParentFolderIds", new XElement(T + "FolderId", new XAttribute("Id", folderId))));

    internal async Task<XElement> CallAsync(XElement operation, CancellationToken cancellation)
    {
        string name = operation.Name.LocalName;
        if (operation.Name.Namespace != M || name is not ("GetFolder" or "FindFolder" or "FindItem"))
            throw new InvalidOperationException("READ_ONLY_OPERATION_BLOCKED");
        var envelope = new XElement(S + "Envelope", new XAttribute(XNamespace.Xmlns + "m", M),
            new XAttribute(XNamespace.Xmlns + "t", T),
            new XElement(S + "Header", new XElement(T + "RequestServerVersion", new XAttribute("Version", "Exchange2013"))),
            new XElement(S + "Body", operation));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("SOAPAction", $"\"{M}/{name}\"");
        request.Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml");
        using var response = await http.SendAsync(request, cancellation);
        Console.WriteLine($"{name}: HTTP {(int)response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Only scheme names, never challenge parameters, cookies, headers or tokens.
            var schemes = response.Headers.WwwAuthenticate.Select(x => x.Scheme)
                .Where(x => x is "Basic" or "Digest" or "NTLM" or "Negotiate" or "Bearer").Distinct();
            Console.WriteLine("인증 방식: " + string.Join(", ", schemes));
        }
        if ((int)response.StatusCode is 401 or 403 or 440)
            throw new CheckException("AUTH_REQUIRED: C# 인증 거부. 회사 EWS 인증 정책 확인 필요.");
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new CheckException("LOGIN_REDIRECT: 로그인 리디렉션 차단. WebView2 세션을 공유하지 않습니다.");
        if (!response.IsSuccessStatusCode) throw new CheckException($"HTTP_{(int)response.StatusCode}");
        if (response.Content.Headers.ContentType?.MediaType is not ("text/xml" or "application/soap+xml"))
            throw new CheckException("INVALID_CONTENT_TYPE: SOAP XML 대신 다른 화면이 반환되었습니다.");
        string xml = await response.Content.ReadAsStringAsync(cancellation);
        return Parse(xml, name);
    }

    internal static XElement Parse(string xml, string operation)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        XDocument document;
        try { document = XDocument.Load(reader); }
        catch (XmlException) { throw new CheckException("INVALID_XML"); }
        var body = document.Root?.Name == S + "Envelope" ? document.Root.Element(S + "Body") : null;
        if (body?.Element(S + "Fault") != null) throw new CheckException("SOAP_FAULT");
        var messages = body?.Element(M + (operation + "Response"))?.Element(M + "ResponseMessages");
        var all = messages?.Elements().ToArray();
        if (all is not { Length: 1 } || all[0].Name != M + (operation + "ResponseMessage"))
            throw new CheckException("INVALID_RESPONSE");
        var message = all[0];
        if ((string?)message.Attribute("ResponseClass") != "Success" || (string?)message.Element(M + "ResponseCode") != "NoError")
        {
            string? code = (string?)message.Element(M + "ResponseCode");
            if (code is "ErrorAccessDenied" or "ErrorInvalidClientAccessToken" or "ErrorInvalidUserSid")
                throw new CheckException("AUTH_REQUIRED: SOAP 인증 거부");
            throw new CheckException("SOAP_ERROR"); // Do not print server text, mailbox data or credentials.
        }
        if (operation == "GetFolder")
        {
            var folders = message.Element(M + "Folders")?.Elements().ToArray();
            if (folders is not { Length: 1 } || folders[0].Name != T + "Folder" ||
                string.IsNullOrEmpty((string?)folders[0].Element(T + "FolderId")?.Attribute("Id")))
                throw new CheckException("INVALID_FOLDER");
        }
        else
        {
            var root = message.Element(M + "RootFolder");
            if (root?.Element(T + (operation == "FindItem" ? "Items" : "Folders")) == null ||
                !bool.TryParse((string?)root.Attribute("IncludesLastItemInRange"), out _) ||
                !int.TryParse((string?)root.Attribute("TotalItemsInView"), out int total) || total < 0)
                throw new CheckException("INVALID_PAGE");
        }
        return message;
    }
}

internal sealed class CheckException(string message) : Exception(message);
