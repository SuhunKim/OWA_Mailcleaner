using System.Net;
using System.Xml.Linq;

namespace OwaDesktopCleaner.Wpf.ConnectionCheck;

internal static class SelfTests
{
    static string Response(string operation, string body, string status = "Success", string code = "NoError") =>
        $"<s:Envelope xmlns:s='{EwsReadClient.S}' xmlns:m='{EwsReadClient.M}' xmlns:t='{EwsReadClient.T}'><s:Body>" +
        $"<m:{operation}Response><m:ResponseMessages><m:{operation}ResponseMessage ResponseClass='{status}'>" +
        $"<m:ResponseCode>{code}</m:ResponseCode>{body}</m:{operation}ResponseMessage></m:ResponseMessages></m:{operation}Response></s:Body></s:Envelope>";
    const string EmptyPage = "<m:RootFolder IncludesLastItemInRange='true' TotalItemsInView='0'><t:Items/></m:RootFolder>";
    static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    static void Reject(Action action, string code)
    {
        try { action(); } catch (CheckException e) when (e.Message.StartsWith(code, StringComparison.Ordinal)) { return; }
        throw new Exception("Expected " + code);
    }

    internal static async Task<int> RunAsync()
    {
        int count = 0;
        async Task Test(string name, Func<Task> action) { await action(); count++; Console.WriteLine("PASS " + name); }
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        try
        {
            await Test("valid empty search", () => Sync(() => EwsReadClient.Parse(Response("FindItem", EmptyPage), "FindItem")));
            await Test("valid folder", () => Sync(() => EwsReadClient.Parse(Response("GetFolder", "<m:Folders><t:Folder><t:FolderId Id='id'/></t:Folder></m:Folders>"), "GetFolder")));
            await Test("valid folder page", () => Sync(() => EwsReadClient.Parse(Response("FindFolder", "<m:RootFolder IncludesLastItemInRange='false' TotalItemsInView='6'><t:Folders/></m:RootFolder>"), "FindFolder")));
            await Test("missing page", () => Sync(() => Reject(() => EwsReadClient.Parse(Response("FindItem", ""), "FindItem"), "INVALID_PAGE")));
            await Test("missing items", () => Sync(() => Reject(() => EwsReadClient.Parse(Response("FindItem", EmptyPage.Replace("<t:Items/>", "")), "FindItem"), "INVALID_PAGE")));
            await Test("invalid paging", () => Sync(() => Reject(() => EwsReadClient.Parse(Response("FindItem", EmptyPage.Replace("'true'", "'bad'")), "FindItem"), "INVALID_PAGE")));
            await Test("missing folder id", () => Sync(() => Reject(() => EwsReadClient.Parse(Response("GetFolder", "<m:Folders><t:Folder/></m:Folders>"), "GetFolder"), "INVALID_FOLDER")));
            await Test("SOAP auth denied", () => Sync(() => Reject(() => EwsReadClient.Parse(Response("FindItem", "", "Error", "ErrorAccessDenied"), "FindItem"), "AUTH_REQUIRED")));
            await Test("SOAP operation error", () => Sync(() => Reject(() => EwsReadClient.Parse(Response("FindItem", "", "Error", "ErrorInvalidIdMalformed"), "FindItem"), "SOAP_ERROR")));
            await Test("HTML instead of SOAP", () => Sync(() => Reject(() => EwsReadClient.Parse("<html/>", "FindItem"), "INVALID_RESPONSE")));
            await Test("DTD forbidden", () => Sync(() => Reject(() => EwsReadClient.Parse("<!DOCTYPE a [<!ENTITY b 'x'>]><a>&b;</a>", "FindItem"), "INVALID_XML")));
            await Test("keyword escaping", () => Sync(() => {
                var query = EwsReadClient.FindItem("id&<", "<&\"'");
                var roundTrip = XElement.Parse(query.ToString());
                Require((string?)roundTrip.Descendants(EwsReadClient.T + "Constant").Single().Attribute("Value") == "<&\"'");
            }));
            foreach (int status in new[] { 401, 403, 440, 302, 500 })
                await Test("HTTP " + status + " no retry", async () => {
                    using var mock = new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
                    using var http = new HttpClient(mock);
                    try { await new EwsReadClient(http).CallAsync(EwsReadClient.GetFolder(), default); throw new Exception("Expected failure"); }
                    catch (CheckException) { Require(mock.Count == 1); }
                });
            await Test("write rejected before transport", async () => {
                using var mock = new MockHandler((_, _) => throw new Exception("Must not send"));
                using var http = new HttpClient(mock);
                try { await new EwsReadClient(http).CallAsync(new XElement(EwsReadClient.M + "DeleteItem"), default); throw new Exception("Expected failure"); }
                catch (InvalidOperationException) { Require(mock.Count == 0); }
            });
            await Test("cancellation", async () => {
                using var mock = new MockHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
                using var http = new HttpClient(mock);
                using var cancel = new CancellationTokenSource();
                var task = new EwsReadClient(http).CallAsync(EwsReadClient.GetFolder(), cancel.Token);
                cancel.Cancel();
                try { await task; throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { Require(mock.Count == 1); }
            });
            await Test("real XML through mocked HTTP", async () => {
                using var mock = new MockHandler((request, _) => {
                    Require(request.RequestUri == EwsReadClient.Endpoint && request.Method == HttpMethod.Post);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent(Response("FindItem", EmptyPage), System.Text.Encoding.UTF8, "text/xml") });
                });
                using var http = new HttpClient(mock);
                await new EwsReadClient(http).CallAsync(EwsReadClient.FindItem("id", "word"), default);
                Require(mock.Count == 1);
            });
            Console.WriteLine($"{count} tests passed. No live server requests.");
            return 0;
        }
        catch (Exception e) { Console.WriteLine($"FAIL after {count}: {e.GetType().Name}"); return 1; }
    }

    sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return send(request, cancellationToken); }
    }
}
