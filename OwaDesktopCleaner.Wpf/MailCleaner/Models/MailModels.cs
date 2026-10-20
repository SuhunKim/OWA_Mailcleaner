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
    public DateTimeOffset? Received { get; init; }
    public string ReceivedDisplay => Received?.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm") ?? "미확인";
    public long? Size { get; init; }
    public string SizeDisplay => Size is {} n ? $"{n / 1048576d:N2} MB" : "미확인";
    public bool HasAttachments { get; init; }
    public bool IsRead { get; init; }
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    public int RemovableCount => Attachments.Count(x => x.Removable);
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
