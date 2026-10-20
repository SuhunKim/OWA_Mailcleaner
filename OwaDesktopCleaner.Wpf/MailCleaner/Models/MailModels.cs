using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MailCleaner.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
}
public enum CleanupMode { Inspect, RemoveAttachments, MarkRead, MoveToDeleted, EmptyDeleted }
public record ModeOption(CleanupMode Value, string Label);
public enum MailSort { DateDesc, DateAsc, SizeDesc, SizeAsc }
public record SortOption(MailSort Value, string Label);
public static class MailDisplay
{
    public static string Size(long? bytes) => bytes is not {} n ? "미확인" : n < 1048576 ? $"{n / 1024d:N1} KB" : $"{n / 1048576d:N1} MB";
    public static string Total(IEnumerable<MailItem> items)
    {
        var all = items.ToArray();
        int unknown = all.Count(x => x.Size is null);
        return Size(all.Sum(x => x.Size ?? 0)) + (unknown > 0 ? $" (용량 미확인 {unknown}건 제외)" : "");
    }
}
public sealed class MailItemComparer(MailSort sort) : System.Collections.IComparer, IComparer<MailItem>
{
    public int Compare(object? x, object? y) => Compare(x as MailItem, y as MailItem);
    public int Compare(MailItem? x, MailItem? y)
    {
        bool size = sort is MailSort.SizeAsc or MailSort.SizeDesc;
        long? a = size ? x?.Size : x?.Received?.UtcTicks;
        long? b = size ? y?.Size : y?.Received?.UtcTicks;
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        return a.Value.CompareTo(b.Value) * (sort is MailSort.SizeDesc or MailSort.DateDesc ? -1 : 1);
    }
}
public record MailFolder(string Id, string ParentId, string Name, string FolderClass)
{
    public string Path { get; init; } = Name;
}
public record AttachmentInfo(string Id, string Kind, string Name, long Size, bool Inline, string ContentId, string ContentLocation)
{
    public bool Removable => Id.Length > 0 && Kind == "FileAttachment" && !Inline && string.IsNullOrWhiteSpace(ContentId) && string.IsNullOrWhiteSpace(ContentLocation);
}
public sealed class MailItem : Observable
{
    public string Id { get; init; } = "";
    public string ChangeKey { get; init; } = "";
    public string ParentId { get; init; } = "";
    public string ItemClass { get; init; } = "";
    public string Subject { get; init; } = "(제목 없음)";
    public string Sender { get; init; } = "";
    public string SenderAddress { get; init; } = "";
    public string SenderAddressDisplay => SenderAddress == Sender ? "" : SenderAddress;
    public DateTimeOffset? Received { get; init; }
    public string ReceivedDisplay => Received?.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm") ?? "미확인";
    public long? Size { get; init; }
    public string SizeDisplay => MailDisplay.Size(Size);
    public string ReadDisplay => IsRead ? "읽음" : "안 읽음";
    public bool HasAttachments { get; init; }
    public bool IsRead { get; init; }
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    public int RemovableCount => Attachments.Count(x => x.Removable);
    public string AttachmentDisplay => RemovableCount > 0 ? $"{RemovableCount}개\n" + string.Join("\n", Attachments.Where(x => x.Removable).Select(x => $"{x.Name} ({MailDisplay.Size(x.Size)})")) : HasAttachments ? "있음 (상세 생략)" : "없음";
    bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
}
public record SearchCriteria(string FolderId, string Keyword, DateTime? Start, DateTime? End, CleanupMode Mode)
{
    public static DateTimeOffset Boundary(DateTime date) => new(DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
}
public enum ResultKind { Success, Failed, Skipped, Unknown, Unprocessed }
public record ProcessingResult(string Subject, ResultKind Kind, string Detail)
{
    public string Label => Kind switch { ResultKind.Success => "성공", ResultKind.Failed => "실패", ResultKind.Skipped => "건너뜀", ResultKind.Unknown => "결과 미확인", _ => "미처리" };
}
public record WorkProgress(int Completed, int Total, string Status);
