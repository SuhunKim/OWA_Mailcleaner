using System.Globalization;
using System.Xml.Linq;
using MailCleaner.Models;
using static MailCleaner.Services.EwsTransport;

namespace MailCleaner.Services;

public interface IMailQuery
{
    Task<MailItem?> GetAsync(string id, CancellationToken ct);
}
public sealed class EwsQueryService(EwsTransport transport) : IMailQuery
{
    public string DeletedFolderId { get; private set; } = "";
    public string InboxFolderId { get; private set; } = "";
    public static XElement Field(string uri) => new(T + "FieldURI", new XAttribute("FieldURI", uri));
    static XElement FolderShape() => new(M + "FolderShape", new XElement(T + "BaseShape", "Default"),
        new XElement(T + "AdditionalProperties", Field("folder:ParentFolderId"), Field("folder:FolderClass")));
    public static XElement ItemShape() => new(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly"), new XElement(T + "AdditionalProperties",
        new[] { "item:ItemClass", "item:Subject", "item:DateTimeReceived", "item:Size", "item:ParentFolderId", "item:HasAttachments", "message:IsRead", "message:From" }.Select(Field)));
    public static XElement Id(string id, string? key = null) => new(T + "ItemId", new XAttribute("Id", id), string.IsNullOrEmpty(key) ? null : new XAttribute("ChangeKey", key));
    async Task<XElement> One(XElement request, CancellationToken ct)
    {
        var messages = await transport.CallAsync(request, ct);
        if (messages.Length != 1) throw new EwsException("응답 개수 오류");
        RequireSuccess(messages[0]); return messages[0];
    }
    static MailFolder Folder(XElement x) => new(Attr(x, "FolderId", "Id"), Attr(x, "ParentFolderId", "Id"), Text(x, "DisplayName"), Text(x, "FolderClass"));
    public async Task<IReadOnlyList<MailFolder>> ConnectAsync(CancellationToken ct)
    {
        async Task<MailFolder> Get(string distinguished)
        {
            var x = await One(new XElement(M + "GetFolder", FolderShape(), new XElement(M + "FolderIds", new XElement(T + "DistinguishedFolderId", new XAttribute("Id", distinguished)))), ct);
            var folders = x.Element(M + "Folders")?.Elements().ToArray();
            if (folders is not { Length: 1 }) throw new EwsException("폴더 응답 미확인");
            var result = Folder(folders[0]);
            if (result.Id.Length == 0) throw new EwsException("폴더 ID 누락");
            return result;
        }
        var inbox = await Get("inbox"); var deleted = await Get("deleteditems");
        var all = new Dictionary<string, MailFolder>();
        int offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var x = await One(new XElement(M + "FindFolder", new XAttribute("Traversal", "Deep"), FolderShape(),
                new XElement(M + "IndexedPageFolderView", new XAttribute("MaxEntriesReturned", 500), new XAttribute("Offset", offset), new XAttribute("BasePoint", "Beginning")),
                new XElement(M + "ParentFolderIds", new XElement(T + "DistinguishedFolderId", new XAttribute("Id", "msgfolderroot")))), ct);
            var root = x.Element(M + "RootFolder") ?? throw new EwsException("폴더 페이지 누락");
            var list = root.Element(T + "Folders") ?? throw new EwsException("폴더 목록 누락");
            foreach (var node in list.Elements().Where(x => x.Name == T + "Folder" || x.Name == T + "SearchFolder"))
            {
                var f = Folder(node);
                if (f.Id.Length > 0 && (f.FolderClass.Length == 0 || f.FolderClass == "IPF.Note")) all[f.Id] = f;
            }
            if (Last(root)) break;
            offset = Next(root, offset, 10000);
        }
        all[inbox.Id] = inbox; all[deleted.Id] = deleted;
        string PathFor(MailFolder f)
        {
            var names = new List<string> { f.Name }; var seen = new HashSet<string> { f.Id };
            while (all.TryGetValue(f.ParentId, out var parent) && seen.Add(parent.Id)) { names.Insert(0, parent.Name); f = parent; }
            return string.Join(" / ", names);
        }
        InboxFolderId = inbox.Id; DeletedFolderId = deleted.Id; transport.MarkAuthenticated();
        return all.Values.Select(x => x with { Path = PathFor(x) }).OrderBy(x => x.Path).ToArray();
    }
    public static XElement SearchRequest(SearchCriteria c, int offset)
    {
        var filters = new List<XElement>();
        void Compare(string op, string field, string value) => filters.Add(new(T + op, Field(field), new XElement(T + "FieldURIOrConstant", new XElement(T + "Constant", new XAttribute("Value", value)))));
        if (c.Start is {} start) Compare("IsGreaterThanOrEqualTo", "item:DateTimeReceived", SearchCriteria.Boundary(start).UtcDateTime.ToString("O"));
        if (c.End is {} end) Compare("IsLessThan", "item:DateTimeReceived", SearchCriteria.Boundary(end.AddDays(1)).UtcDateTime.ToString("O"));
        if (c.Keyword.Trim().Length > 0) filters.Add(new(T + "Contains", new XAttribute("ContainmentMode", "Substring"), new XAttribute("ContainmentComparison", "IgnoreCase"), Field("item:Subject"), new XElement(T + "Constant", new XAttribute("Value", c.Keyword.Trim()))));
        if (c.Mode == CleanupMode.RemoveAttachments) Compare("IsEqualTo", "item:HasAttachments", "true");
        if (c.Mode == CleanupMode.MarkRead) Compare("IsEqualTo", "message:IsRead", "false");
        return new(M + "FindItem", new XAttribute("Traversal", "Shallow"), ItemShape(),
            new XElement(M + "IndexedPageItemView", new XAttribute("MaxEntriesReturned", 200), new XAttribute("Offset", offset), new XAttribute("BasePoint", "Beginning")),
            filters.Count == 0 ? null : new XElement(M + "Restriction", filters.Count == 1 ? filters[0] : new XElement(T + "And", filters)),
            new XElement(M + "SortOrder", new XElement(T + "FieldOrder", new XAttribute("Order", "Descending"), Field("item:DateTimeReceived"))),
            new XElement(M + "ParentFolderIds", new XElement(T + "FolderId", new XAttribute("Id", c.FolderId))));
    }
    public async Task<IReadOnlyList<MailItem>> SearchAsync(SearchCriteria criteria, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        if (criteria.Start > criteria.End) throw new EwsException("시작일이 종료일보다 늦습니다");
        int offset = 0; var all = new Dictionary<string, MailItem>();
        while (true)
        {
            var response = await One(SearchRequest(criteria, offset), ct);
            var root = response.Element(M + "RootFolder") ?? throw new EwsException("검색 페이지 누락");
            var items = root.Element(T + "Items") ?? throw new EwsException("메일 목록 누락");
            foreach (var x in items.Elements(T + "Message"))
            {
                var mail = ParseMail(x);
                if (mail.Id.Length == 0) throw new EwsException("메일 ID 누락");
                if (!mail.ItemClass.StartsWith("IPM.Note", StringComparison.Ordinal)) continue;
                if (criteria.Mode == CleanupMode.MarkRead && mail.IsRead) continue;
                all[mail.Id] = mail;
            }
            int.TryParse((string?)root.Attribute("TotalItemsInView"), out int total);
            progress?.Report(new(all.Count, total, $"메일 검색 · {all.Count:N0}건"));
            if (Last(root)) break;
            offset = Next(root, offset, 100000);
        }
        if (criteria.Mode == CleanupMode.RemoveAttachments)
        {
            var detailed = new List<MailItem>(); int count = 0;
            foreach (var mail in all.Values)
            {
                ct.ThrowIfCancellationRequested();
                var current = await GetAsync(mail.Id, ct);
                if (current is not null && current.ParentId == criteria.FolderId && current.RemovableCount > 0) detailed.Add(current);
                progress?.Report(new(++count, all.Count, $"일반 첨부 확인 · {count:N0}/{all.Count:N0}"));
            }
            return detailed;
        }
        return all.Values.ToArray();
    }
    public async Task<MailItem?> GetAsync(string id, CancellationToken ct)
    {
        var shape = ItemShape(); shape.Element(T + "AdditionalProperties")!.Add(Field("item:Attachments"));
        var messages = await transport.CallAsync(new XElement(M + "GetItem", shape, new XElement(M + "ItemIds", Id(id))), ct);
        if (messages.Length != 1) throw new EwsException("메일 응답 수 미확인");
        if ((string?)messages[0].Element(M + "ResponseCode") == "ErrorItemNotFound") return null;
        RequireSuccess(messages[0]);
        var items = messages[0].Element(M + "Items")?.Elements().ToArray();
        if (items is not { Length: 1 } || items[0].Name != T + "Message") return null;
        return ParseMail(items[0]);
    }
    public static MailItem ParseMail(XElement x) => new()
    {
        Id = Attr(x, "ItemId", "Id"), ChangeKey = Attr(x, "ItemId", "ChangeKey"), ParentId = Attr(x, "ParentFolderId", "Id"),
        ItemClass = Text(x, "ItemClass"), Subject = Text(x, "Subject") is { Length: > 0 } subject ? subject : "(제목 없음)",
        Sender = (string?)x.Element(T + "From")?.Element(T + "Mailbox")?.Element(T + "Name") ?? "",
        SenderAddress = (string?)x.Element(T + "From")?.Element(T + "Mailbox")?.Element(T + "EmailAddress") ?? "",
        Received = DateTimeOffset.TryParse(Text(x, "DateTimeReceived"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null,
        Size = long.TryParse(Text(x, "Size"), out long size) && size >= 0 ? size : null,
        HasAttachments = Text(x, "HasAttachments") == "true", IsRead = Text(x, "IsRead") == "true",
        Attachments = x.Element(T + "Attachments")?.Elements().Select(a => new AttachmentInfo(Attr(a, "AttachmentId", "Id"), a.Name.LocalName, Text(a, "Name"), long.TryParse(Text(a, "Size"), out long n) ? n : 0,
            Text(a, "IsInline") == "true", Text(a, "ContentId"), Text(a, "ContentLocation"))).ToArray() ?? []
    };
    static bool Last(XElement root) => bool.TryParse((string?)root.Attribute("IncludesLastItemInRange"), out bool last) ? last : throw new EwsException("페이지 종료 정보 누락");
    static int Next(XElement root, int previous, int limit)
    {
        if (!int.TryParse((string?)root.Attribute("IndexedPagingOffset"), out int next) || next <= previous || next >= limit) throw new EwsException("페이지 진행 오류 또는 조회 상한 · 기간을 좁히세요");
        return next;
    }
    static string Attr(XElement x, string element, string attribute) => (string?)x.Element(T + element)?.Attribute(attribute) ?? "";
    static string Text(XElement x, string element) => (string?)x.Element(T + element) ?? "";
}
