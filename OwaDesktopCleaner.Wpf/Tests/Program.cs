using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using MailCleaner.Models;
using MailCleaner.Services;
using MailCleaner.ViewModels;
using static MailCleaner.Services.EwsTransport;

if (args.Length == 2 && args[0] == "--ui-smoke") return UiSmoke.Run(args[1]);
if (args.SequenceEqual(new[] { "--live-read" }))
{
    using var transport = new AuthenticationService().Create();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var query = new EwsQueryService(transport);
    var folders = await query.ConnectAsync(timeout.Token);
    Console.WriteLine($"WPF C# query service: folders={folders.Count}; authenticated={transport.Authenticated}; writes={transport.AllowChanges}");
    var items = await query.SearchAsync(new(query.DeletedFolderId, "", DateTime.Today, DateTime.Today, CleanupMode.Inspect), null, timeout.Token);
    Console.WriteLine($"WPF C# query service: today's deleted-folder search={items.Count}; no mutations");
    return 0;
}
int count = 0;
async Task Test(string name, Func<Task> run) { await run(); Console.WriteLine("PASS " + name); count++; }
Task Sync(Action run) { run(); return Task.CompletedTask; }
void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
async Task Reject<T>(Func<Task> run) where T : Exception { try { await run(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
MailItem Mail(string id = "id", string key = "latest", string parent = "folder", string kind = "IPM.Note", bool read = false, AttachmentInfo[]? attachments = null) => new()
{ Id = id, ChangeKey = key, ParentId = parent, ItemClass = kind, Subject = "mock", IsRead = read, Attachments = attachments ?? [] };
SearchCriteria Criteria(CleanupMode mode = CleanupMode.MarkRead) => new("folder", "", null, null, mode);
SearchSession Session(SearchCriteria c, params MailItem[] items) { var s = new SearchSession(); s.Complete(c, items); return s; }
string Response(string name, string body = "", string responseClass = "Success", string code = "NoError") => $"<s:Envelope xmlns:s='{S}' xmlns:m='{M}' xmlns:t='{T}'><s:Body><m:{name}Response><m:ResponseMessages><m:{name}ResponseMessage ResponseClass='{responseClass}'><m:ResponseCode>{code}</m:ResponseCode>{body}</m:{name}ResponseMessage></m:ResponseMessages></m:{name}Response></s:Body></s:Envelope>";
HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
try
{
    string MockMail() => "<t:Message><t:ItemId Id='id' ChangeKey='key'/><t:ParentFolderId Id='folder'/><t:ItemClass>IPM.Note</t:ItemClass><t:Subject>mock</t:Subject><t:Size>1024</t:Size><t:IsRead>false</t:IsRead></t:Message>";
    Func<NetworkCredential?, EwsTransport> VmFactory(Func<bool>? authFailure = null) => _ => new EwsTransport(new HttpClient(new MockHandler(async (request, ct) => {
        if (authFailure?.Invoke() == true) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var doc = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var op = doc.Root!.Element(S + "Body")!.Elements().Single();
        string name = op.Name.LocalName;
        string folderId = (string?)op.Descendants(T + "DistinguishedFolderId").FirstOrDefault()?.Attribute("Id") == "deleteditems" ? "deleted" : "folder";
        string body = name switch {
            "GetFolder" => $"<m:Folders><t:Folder><t:FolderId Id='{folderId}'/><t:DisplayName>mock</t:DisplayName><t:FolderClass>IPF.Note</t:FolderClass></t:Folder></m:Folders>",
            "FindFolder" => "<m:RootFolder IncludesLastItemInRange='true' TotalItemsInView='0'><t:Folders/></m:RootFolder>",
            "FindItem" => "<m:RootFolder IncludesLastItemInRange='true' TotalItemsInView='1'><t:Items>" + MockMail() + "</t:Items></m:RootFolder>",
            "GetItem" or "UpdateItem" => "<m:Items>" + MockMail() + "</m:Items>", _ => throw new Exception("Unexpected " + name) };
        return Xml(Response(name, body));
    })));
    await Test("ViewModel default allow, persistent opt-out, condition selection invalidation", async () => {
        var store = new FakeSettings(); using var vm = new MainViewModel(new FakeDialogs(), store, VmFactory());
        await vm.ConnectAsync(null); Require(vm.Connected && vm.AllowChanges && vm.Mode.Value == CleanupMode.Inspect);
        vm.AllowChanges = false; await vm.ConnectAsync(null); Require(!vm.AllowChanges && store.Settings.AllowChanges == false);
        vm.AllowChanges = true; vm.Mode = vm.Modes.Single(x => x.Value == CleanupMode.MarkRead);
        await vm.SearchAsync(); vm.Mails[0].Selected = true; Require(vm.ExecuteCommand.CanExecute(null));
        vm.DateTextEdited(false); Require(!vm.SearchCommand.CanExecute(null) && !vm.ExecuteCommand.CanExecute(null) && !vm.Mails[0].Selected);
        vm.DateTextEdited(true); await vm.SearchAsync(); vm.Mails[0].Selected = true;
        vm.Keyword = "changed"; Require(!vm.Mails[0].Selected && !vm.ExecuteCommand.CanExecute(null));
        await vm.SearchAsync(); vm.Mails[0].Selected = true; vm.Mode = vm.Modes[0]; Require(!vm.ExecuteCommand.CanExecute(null) && !vm.Mails[0].Selected);
    });
    await Test("ViewModel final confirmation, collapsed report, next search hides report", async () => {
        var dialogs = new FakeDialogs(); using var vm = new MainViewModel(dialogs, new FakeSettings(), VmFactory());
        await vm.ConnectAsync(null); vm.Mode = vm.Modes.Single(x => x.Value == CleanupMode.MarkRead);
        await vm.SearchAsync(); vm.Mails[0].Selected = true;
        dialogs.Accept = false; await vm.ExecuteAsync(); Require(vm.Mails.Count == 1 && vm.Results.Count == 0);
        dialogs.Accept = true; await vm.ExecuteAsync(); Require(dialogs.Confirmations == 2 && vm.Results.Single().Kind == ResultKind.Success && vm.ReportVisible && !vm.ReportExpanded && vm.Mails.Count == 0);
        await vm.SearchAsync(); Require(!vm.ReportVisible && vm.Results.Count == 0);
    });
    await Test("ViewModel authentication expiration disables writes", async () => {
        bool expired = false; using var vm = new MainViewModel(new FakeDialogs(), new FakeSettings(), VmFactory(() => expired));
        await vm.ConnectAsync(null); Require(vm.AllowChanges); expired = true;
        await Reject<EwsException>(() => vm.SearchAsync()); Require(!vm.Connected && !vm.AllowChanges && !vm.ExecuteCommand.CanExecute(null));
    });
    await Test("UI mode chips and display sort preserve execution safeguards", async () => {
        using var vm = new MainViewModel(new FakeDialogs(), new FakeSettings(), VmFactory());
        await vm.ConnectAsync(null); await vm.SearchAsync(); vm.Mails[0].Selected = true;
        vm.SelectedSort = vm.SortOptions.Single(x => x.Value == MailSort.SizeDesc);
        Require(vm.Mails[0].Selected);
        vm.SelectedMode = CleanupMode.MarkRead;
        Require(!vm.Mails[0].Selected && !vm.ExecuteCommand.CanExecute(null));
        string previousFolder = vm.Folder!.Id; string previousKeyword = vm.Keyword;
        vm.SelectedMode = CleanupMode.EmptyDeleted;
        Require(vm.Folder!.Id == "deleted" && vm.Keyword == "" && !vm.CanChooseFolder && vm.ExecuteLabel == "선택 항목 비우기");
        await vm.ConnectAsync(null); Require(vm.Folder!.Id == "deleted" && !vm.CanChooseFolder);
        vm.SelectedMode = CleanupMode.Inspect;
        Require(vm.Folder!.Id == previousFolder && vm.Keyword == previousKeyword && vm.CanChooseFolder && !vm.ExecuteCommand.CanExecute(null));
    });
    await Test("all display sorts keep unknown values last", () => Sync(() => {
        var a = new MailItem { Id = "small", Size = 1024, Received = DateTimeOffset.Parse("2026-10-19T01:00:00Z") };
        var b = new MailItem { Id = "large", Size = 2097152, Received = DateTimeOffset.Parse("2026-10-20T01:00:00Z") };
        var unknown = new MailItem { Id = "unknown" };
        foreach (var sort in Enum.GetValues<MailSort>())
        {
            var ordered = new[] { unknown, a, b }.OrderBy(x => x, new MailItemComparer(sort)).ToArray();
            Require(ordered[^1] == unknown && ordered[0] == (sort is MailSort.SizeDesc or MailSort.DateDesc ? b : a));
        }
        Require(MailDisplay.Size(1024) == "1.0 KB" && MailDisplay.Size(null) == "미확인");
    }));
    await Test("condition and mode changes invalidate execution", () => Sync(() => {
        var c = Criteria(); var s = Session(c, Mail());
        foreach (var changed in new[] { c with { FolderId = "other" }, c with { Keyword = "a" }, c with { Start = DateTime.Today }, c with { End = DateTime.Today }, c with { Mode = CleanupMode.EmptyDeleted } }) Require(!s.Matches(changed));
        s.Invalidate(); Require(!s.Matches(c));
    }));
    await Test("foreign selection and duplicate IDs blocked", async () => {
        var c = Criteria(); var m = Mail(); var s = Session(c, m);
        await Reject<EwsException>(() => Sync(() => s.Consume(c, [Mail("foreign")])));
        await Reject<EwsException>(() => Sync(() => s.Consume(c, [m, m])));
        Require(s.Available);
    });
    await Test("one-shot execution blocks duplicate mutation", async () => {
        var m = Mail(); var c = Criteria(); var s = Session(c, m); var writer = new FakeWriter();
        var service = new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(m)), writer);
        var results = await service.ExecuteAsync(s, c, [m], "deleted", null, default);
        Require(results.Single().Kind == ResultKind.Success && writer.Count == 1);
        await Reject<EwsException>(() => service.ExecuteAsync(s, c, [m], "deleted", null, default)); Require(writer.Count == 1);
    });
    await Test("identity parent kind and latest key validated", async () => {
        foreach (var current in new[] { Mail("changed"), Mail(parent: "moved"), Mail(kind: "IPM.Appointment"), Mail(key: "") })
        {
            var original = Mail(); var c = Criteria(); var writer = new FakeWriter();
            var service = new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(current)), writer);
            var result = await service.ExecuteAsync(Session(c, original), c, [original], "deleted", null, default);
            Require(result[0].Kind == ResultKind.Skipped && writer.Count == 0);
        }
    });
    await Test("uses fresh ChangeKey and suppresses read receipts", async () => {
        var m = Mail(key: "old"); var c = Criteria(); var writer = new FakeWriter();
        await new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(Mail(key: "new"))), writer).ExecuteAsync(Session(c, m), c, [m], "deleted", null, default);
        Require(writer.Last?.ChangeKey == "new");
        var xml = EwsMailWriter.Build(writer.Last!, c.Mode, []);
        Require((string?)xml.Attribute("SuppressReadReceipts") == "true" && (string?)xml.Descendants(T + "ItemId").Single().Attribute("ChangeKey") == "new");
    });
    await Test("attachment intersection excludes inline linked and item attachments", async () => {
        AttachmentInfo A(string id, string kind = "FileAttachment", bool inline = false, string cid = "", string location = "") => new(id, kind, "file", 1, inline, cid, location);
        var old = Mail(attachments: [A("keep"), A("gone"), A("inline"), A("cid"), A("location"), A("item")]);
        var current = Mail(attachments: [A("keep"), A("new"), A("inline", inline: true), A("cid", cid: "cid"), A("location", location: "url"), A("item", kind: "ItemAttachment")]);
        var c = Criteria(CleanupMode.RemoveAttachments); var writer = new FakeWriter();
        await new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(current)), writer).ExecuteAsync(Session(c, old), c, [old], "deleted", null, default);
        Require(writer.Attachments.Select(x => x.Id).SequenceEqual(new[] { "keep" }));
    });
    await Test("SoftDelete and deleted folder guard", async () => {
        var m = Mail(); var c = Criteria(CleanupMode.EmptyDeleted); var writer = new FakeWriter();
        var service = new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(m)), writer);
        await Reject<EwsException>(() => service.ExecuteAsync(Session(c, m), c, [m], "other", null, default));
        Require(writer.Count == 0);
        Require((string?)EwsMailWriter.Build(m, c.Mode, []).Attribute("DeleteType") == "SoftDelete");
    });
    await Test("partial known failures continue and remain distinct", async () => {
        var a = Mail("a"); var b = Mail("b"); var c = Criteria();
        var writer = new FakeWriter { Run = m => Task.FromResult(new ProcessingResult(m.Subject, m.Id == "a" ? ResultKind.Failed : ResultKind.Success, "mock")) };
        var results = await new MailMutationService(new FakeQuery((id, _) => Task.FromResult<MailItem?>(id == "a" ? a : b)), writer).ExecuteAsync(Session(c, a, b), c, [a, b], "deleted", null, default);
        Require(results.Select(x => x.Kind).SequenceEqual(new[] { ResultKind.Failed, ResultKind.Success }));
    });
    await Test("unknown outcome stops without retry and marks remaining unprocessed", async () => {
        var a = Mail("a"); var b = Mail("b"); var c = Criteria(); var writer = new FakeWriter { Run = _ => throw new MutationUnknownException() };
        var results = await new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(a)), writer).ExecuteAsync(Session(c, a, b), c, [a, b], "deleted", null, default);
        Require(writer.Count == 1 && results[0].Kind == ResultKind.Unknown && results[1].Kind == ResultKind.Unprocessed);
    });
    await Test("stop during preflight sends no mutation", async () => {
        var m = Mail(); var c = Criteria(); using var stop = new CancellationTokenSource(); var writer = new FakeWriter();
        var query = new FakeQuery((_, _) => { stop.Cancel(); return Task.FromResult<MailItem?>(m); });
        var results = await new MailMutationService(query, writer).ExecuteAsync(Session(c, m), c, [m], "deleted", null, stop.Token);
        Require(writer.Count == 0 && results.Single().Kind == ResultKind.Unprocessed);
    });
    await Test("stop after submitted mutation waits then halts", async () => {
        var a = Mail("a"); var b = Mail("b"); var c = Criteria(); using var stop = new CancellationTokenSource();
        var writer = new FakeWriter { Run = async m => { stop.Cancel(); await Task.Yield(); return new(m.Subject, ResultKind.Success, "done"); } };
        var results = await new MailMutationService(new FakeQuery((_, _) => Task.FromResult<MailItem?>(a)), writer).ExecuteAsync(Session(c, a, b), c, [a, b], "deleted", null, stop.Token);
        Require(writer.Count == 1 && results[0].Kind == ResultKind.Success && results[1].Kind == ResultKind.Unprocessed);
    });
    await Test("concurrent execution rejected", async () => {
        var m = Mail(); var c = Criteria(); var gate = new TaskCompletionSource<MailItem?>();
        var service = new MailMutationService(new FakeQuery((_, _) => gate.Task), new FakeWriter()); var s = Session(c, m);
        var first = service.ExecuteAsync(s, c, [m], "deleted", null, default);
        await Reject<EwsException>(() => service.ExecuteAsync(s, c, [m], "deleted", null, default)); gate.SetResult(m); await first;
    });
    await Test("HTTP auth failure blocks later writes", async () => {
        using var mock = new MockHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var transport = new EwsTransport(new HttpClient(mock)); transport.MarkAuthenticated(); transport.AllowChanges = true;
        await Reject<EwsException>(() => transport.CallAsync(new XElement(M + "GetItem"), default));
        Require(!transport.Authenticated && !transport.AllowChanges);
        await Reject<EwsException>(() => transport.CallAsync(EwsMailWriter.Build(Mail(), CleanupMode.MarkRead, []), default)); Require(mock.Count == 1);
    });
    foreach (var fault in new[] { "network", "missing", "html", "auth", "soapauth" })
        await Test("mutation " + fault + " is unknown; no automatic retry", async () => {
            using var mock = new MockHandler((_, _) => fault switch {
                "network" => throw new HttpRequestException(),
                "html" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>") }),
                "auth" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
                "soapauth" => Task.FromResult(Xml(Response("UpdateItem", "", "Error", "ErrorAccessDenied"))),
                _ => Task.FromResult(Xml("<s:Envelope xmlns:s='" + S + "'><s:Body/></s:Envelope>")) });
            using var transport = new EwsTransport(new HttpClient(mock)); transport.MarkAuthenticated(); transport.AllowChanges = true;
            await Reject<MutationUnknownException>(() => new EwsMailWriter(transport).WriteAsync(Mail(), CleanupMode.MarkRead, [])); Require(mock.Count == 1);
        });
    await Test("known SOAP mutation failure is failure", async () => {
        using var mock = new MockHandler((_, _) => Task.FromResult(Xml(Response("UpdateItem", "", "Error", "ErrorInvalidChangeKey"))));
        using var transport = new EwsTransport(new HttpClient(mock)); transport.MarkAuthenticated(); transport.AllowChanges = true;
        var result = await new EwsMailWriter(transport).WriteAsync(Mail(), CleanupMode.MarkRead, []); Require(result.Kind == ResultKind.Failed && mock.Count == 1);
    });
    await Test("mock search parses actual SOAP and dates keyword escape", async () => {
        using var mock = new MockHandler(async (request, _) => {
            var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            Require(xml.Descendants(T + "Constant").Any(x => (string?)x.Attribute("Value") == "<&\""));
            Require(xml.Descendants(T + "Constant").Any(x => ((string?)x.Attribute("Value"))?.StartsWith("2026-10-19T15:00:00") == true));
            return Xml(Response("FindItem", "<m:RootFolder IncludesLastItemInRange='true' TotalItemsInView='1'><t:Items><t:Message><t:ItemId Id='id' ChangeKey='key'/><t:ItemClass>IPM.Note</t:ItemClass><t:ParentFolderId Id='folder'/><t:Subject>mock</t:Subject><t:Size>1024</t:Size></t:Message></t:Items></m:RootFolder>"));
        });
        using var transport = new EwsTransport(new HttpClient(mock));
        var items = await new EwsQueryService(transport).SearchAsync(new("folder", "<&\"", new(2026, 10, 20), new(2026, 10, 20), CleanupMode.Inspect), null, default);
        Require(items.Single().Size == 1024 && items.Single().ChangeKey == "key");
    });
    await Test("search paging loops rejected", async () => {
        using var mock = new MockHandler((_, _) => Task.FromResult(Xml(Response("FindItem", "<m:RootFolder IncludesLastItemInRange='false' IndexedPagingOffset='0' TotalItemsInView='1'><t:Items/></m:RootFolder>"))));
        using var transport = new EwsTransport(new HttpClient(mock));
        await Reject<EwsException>(() => new EwsQueryService(transport).SearchAsync(Criteria(), null, default)); Require(mock.Count == 1);
    });
    await Test("search cancellation reaches transport", async () => {
        using var mock = new MockHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Xml(""); });
        using var transport = new EwsTransport(new HttpClient(mock)); using var cancel = new CancellationTokenSource();
        var task = new EwsQueryService(transport).SearchAsync(Criteria(), null, cancel.Token); cancel.Cancel();
        await Reject<OperationCanceledException>(() => task); Require(mock.Count == 1);
    });
    await Test("DTD and malformed SOAP rejected", async () => {
        foreach (string xml in new[] { "<!DOCTYPE a [<!ENTITY x 'b'>]><a>&x;</a>", "<html/>" })
        {
            using var mock = new MockHandler((_, _) => Task.FromResult(Xml(xml)));
            using var transport = new EwsTransport(new HttpClient(mock));
            try { await transport.CallAsync(new XElement(M + "GetItem"), default); throw new Exception("Expected rejection"); }
            catch (System.Xml.XmlException) { } catch (EwsException) { }
        }
    });
    Console.WriteLine($"{count} tests passed; mock only; no mailbox changes."); return 0;
}
catch (Exception e) { Console.WriteLine($"FAIL after {count}: {e}"); return 1; }

sealed class FakeQuery(Func<string, CancellationToken, Task<MailItem?>> run) : IMailQuery
{ public Task<MailItem?> GetAsync(string id, CancellationToken ct) => run(id, ct); }
sealed class FakeWriter : IMailWriter
{
    public int Count; public MailItem? Last; public IReadOnlyList<AttachmentInfo> Attachments = [];
    public Func<MailItem, Task<ProcessingResult>> Run = m => Task.FromResult(new ProcessingResult(m.Subject, ResultKind.Success, "mock"));
    public Task<ProcessingResult> WriteAsync(MailItem current, CleanupMode mode, IReadOnlyList<AttachmentInfo> attachments) { Count++; Last = current; Attachments = attachments; return Run(current); }
}
sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Count;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Count++; return send(request, cancellationToken); }
}
sealed class FakeSettings : IUserSettingsStore
{
    public UserSettings Settings = new();
    public UserSettings Load() => Settings;
    public void Save(UserSettings settings) => Settings = settings;
}
sealed class FakeDialogs : IUserDialogs
{
    public bool Accept = true;
    public int Confirmations;
    public bool Confirm(string message) { Confirmations++; return Accept; }
    public NetworkCredential? Credentials() => null;
}
