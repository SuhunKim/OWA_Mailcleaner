using System.Xml.Linq;
using MailCleaner.Models;
using static MailCleaner.Services.EwsTransport;

namespace MailCleaner.Services;

public sealed class SearchSession
{
    public SearchCriteria? Criteria { get; private set; }
    public bool Available { get; private set; }
    public IReadOnlyList<MailItem> Items { get; private set; } = [];
    public void Complete(SearchCriteria criteria, IReadOnlyList<MailItem> items) { Criteria = criteria; Items = items.ToArray(); Available = true; }
    public void Invalidate() { Available = false; Items = []; }
    public bool Matches(SearchCriteria criteria) => Available && Criteria == criteria;
    public IReadOnlyList<MailItem> Consume(SearchCriteria criteria, IReadOnlyList<MailItem> selected)
    {
        if (!Matches(criteria) || selected.Count == 0 || selected.Any(x => !Items.Contains(x)) || selected.Select(x => x.Id).Distinct().Count() != selected.Count)
            throw new EwsException("검색·선택이 유효하지 않습니다. 다시 검색하세요");
        var snapshot = selected.ToArray(); Invalidate(); return snapshot;
    }
}
public interface IMailWriter
{
    Task<ProcessingResult> WriteAsync(MailItem current, CleanupMode mode, IReadOnlyList<AttachmentInfo> attachments);
}
public sealed class EwsMailWriter(EwsTransport transport) : IMailWriter
{
    public static XElement Build(MailItem current, CleanupMode mode, IReadOnlyList<AttachmentInfo> attachments)
    {
        var id = EwsQueryService.Id(current.Id, current.ChangeKey);
        return mode switch
        {
            CleanupMode.MarkRead => new(M + "UpdateItem", new XAttribute("ConflictResolution", "NeverOverwrite"), new XAttribute("MessageDisposition", "SaveOnly"), new XAttribute("SuppressReadReceipts", "true"),
                new XElement(M + "ItemChanges", new XElement(T + "ItemChange", id, new XElement(T + "Updates", new XElement(T + "SetItemField", EwsQueryService.Field("message:IsRead"), new XElement(T + "Message", new XElement(T + "IsRead", "true"))))))),
            CleanupMode.MoveToDeleted => new(M + "MoveItem", new XElement(M + "ToFolderId", new XElement(T + "DistinguishedFolderId", new XAttribute("Id", "deleteditems"))), new XElement(M + "ItemIds", id)),
            CleanupMode.EmptyDeleted => new(M + "DeleteItem", new XAttribute("DeleteType", "SoftDelete"), new XAttribute("SendMeetingCancellations", "SendToNone"), new XElement(M + "ItemIds", id)),
            CleanupMode.RemoveAttachments when attachments.Count > 0 && attachments.All(x => x.Removable) => new(M + "DeleteAttachment", new XElement(M + "AttachmentIds", attachments.Select(x => new XElement(T + "AttachmentId", new XAttribute("Id", x.Id))))),
            _ => throw new EwsException("처리 방식 또는 첨부 선택 오류")
        };
    }
    public async Task<ProcessingResult> WriteAsync(MailItem current, CleanupMode mode, IReadOnlyList<AttachmentInfo> attachments)
    {
        var request = Build(current, mode, attachments);
        // Stop requests affect the next item. Do not abort an already submitted mutation.
        var messages = await transport.CallAsync(request, CancellationToken.None);
        int expected = mode == CleanupMode.RemoveAttachments ? attachments.Count : 1;
        if (messages.Length != expected || messages.Any(x => x.Element(M + "ResponseCode") is null || x.Attribute("ResponseClass") is null)) throw new MutationUnknownException();
        int succeeded = messages.Count(x => (string?)x.Attribute("ResponseClass") == "Success" && (string?)x.Element(M + "ResponseCode") == "NoError");
        if (succeeded == expected) return new(current.Subject, ResultKind.Success, mode == CleanupMode.RemoveAttachments ? $"일반 첨부 {expected}개 삭제" : "EWS NoError");
        var codes = messages.Select(x => (string?)x.Element(M + "ResponseCode") ?? "").Where(x => x.StartsWith("Error") && x.All(char.IsAsciiLetterOrDigit)).Distinct();
        return new(current.Subject, ResultKind.Failed, $"성공 {succeeded}/{expected} · {string.Join(", ", codes)}");
    }
}
public sealed class MailMutationService(IMailQuery query, IMailWriter writer)
{
    int running;
    public async Task<IReadOnlyList<ProcessingResult>> ExecuteAsync(SearchSession session, SearchCriteria criteria, IReadOnlyList<MailItem> selected,
        string deletedFolderId, IProgress<WorkProgress>? progress, CancellationToken stop)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new EwsException("이미 처리 중입니다");
        try
        {
            if (criteria.Mode == CleanupMode.Inspect || (criteria.Mode == CleanupMode.EmptyDeleted && criteria.FolderId != deletedFolderId)) throw new EwsException("처리 방식·휴지통 폴더 확인 필요");
            var items = session.Consume(criteria, selected);
            var results = new List<ProcessingResult>();
            foreach (var original in items)
            {
                if (stop.IsCancellationRequested) break;
                bool halt = false;
                try
                {
                    var current = await query.GetAsync(original.Id, stop);
                    if (stop.IsCancellationRequested) break;
                    if (current is null || current.Id != original.Id || current.ParentId != criteria.FolderId || !current.ItemClass.StartsWith("IPM.Note", StringComparison.Ordinal))
                        results.Add(new(original.Subject, ResultKind.Skipped, "위치·항목 종류 변경 또는 항목 없음"));
                    else if (string.IsNullOrWhiteSpace(current.ChangeKey))
                        results.Add(new(original.Subject, ResultKind.Skipped, "최신 ChangeKey 확인 불가"));
                    else if (criteria.Mode == CleanupMode.MarkRead && current.IsRead)
                        results.Add(new(original.Subject, ResultKind.Skipped, "이미 읽음"));
                    else
                    {
                        var snapshotIds = original.Attachments.Where(x => x.Removable).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                        var intersection = current.Attachments.Where(x => x.Removable && snapshotIds.Contains(x.Id)).DistinctBy(x => x.Id).ToArray();
                        if (criteria.Mode == CleanupMode.RemoveAttachments && intersection.Length == 0)
                            results.Add(new(original.Subject, ResultKind.Skipped, "선택 당시 일반 첨부와 일치하는 첨부 없음"));
                        else results.Add(await writer.WriteAsync(current, criteria.Mode, intersection));
                    }
                }
                catch (MutationUnknownException) { results.Add(new(original.Subject, ResultKind.Unknown, "서버 결과를 재조회하세요 · 자동 재시도 중단")); halt = true; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (EwsException ex) { results.Add(new(original.Subject, ResultKind.Failed, ex.Code)); halt = true; }
                catch (Exception) { results.Add(new(original.Subject, ResultKind.Failed, "변경 전 조회 실패")); halt = true; }
                progress?.Report(new(results.Count, items.Count, $"처리 {results.Count:N0}/{items.Count:N0}"));
                if (halt) break;
            }
            foreach (var remaining in items.Skip(results.Count)) results.Add(new(remaining.Subject, ResultKind.Unprocessed, "중단 요청 또는 앞선 요청 오류"));
            return results;
        }
        finally { Volatile.Write(ref running, 0); }
    }
}
