using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using MailCleaner.Models;
using MailCleaner.Services;

namespace MailCleaner.ViewModels;

public interface IUserDialogs
{
    bool Confirm(string message);
    NetworkCredential? Credentials();
}
public sealed class MainViewModel : Observable, IDisposable
{
    readonly IUserDialogs dialogs;
    readonly IUserSettingsStore settingsService;
    readonly Func<NetworkCredential?, EwsTransport> createTransport;
    readonly SearchSession session = new();
    UserSettings settings = new();
    EwsTransport? transport;
    EwsQueryService? query;
    MailMutationService? mutations;
    CancellationTokenSource? cancellation;
    public ObservableCollection<MailFolder> Folders { get; } = [];
    public ObservableCollection<MailItem> Mails { get; } = [];
    public ObservableCollection<ProcessingResult> Results { get; } = [];
    public IReadOnlyList<ModeOption> Modes { get; } = [new(CleanupMode.Inspect, "조회만"), new(CleanupMode.RemoveAttachments, "일반 첨부 삭제"), new(CleanupMode.MarkRead, "읽음 처리"), new(CleanupMode.MoveToDeleted, "휴지통 이동"), new(CleanupMode.EmptyDeleted, "휴지통 정리 (SoftDelete)")];
    public IReadOnlyList<SortOption> SortOptions { get; } = [new(MailSort.DateDesc, "수신일 최신순"), new(MailSort.DateAsc, "수신일 오래된 순"), new(MailSort.SizeDesc, "용량 큰 순"), new(MailSort.SizeAsc, "용량 작은 순")];
    SortOption selectedSort = new(MailSort.DateDesc, "수신일 최신순");
    public SortOption SelectedSort { get => selectedSort; set { if (value != null) Set(ref selectedSort, value); } }
    SearchCriteria? previousCriteria;
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand ExecuteCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public MainViewModel(IUserDialogs dialogs, IUserSettingsStore? settingsStore = null, Func<NetworkCredential?, EwsTransport>? transportFactory = null)
    {
        this.dialogs = dialogs;
        settingsService = settingsStore ?? new SettingsService();
        createTransport = transportFactory ?? new AuthenticationService().Create;
        settings = settingsService.Load();
        mode = Modes[0];
        selectedSort = SortOptions[0];
        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date;
        keyword = "일보"; start = today.AddDays(-29); end = today;
        ConnectCommand = new(() => ConnectAsync(null), () => !Busy, Failure);
        LoginCommand = new(async () => { var credential = dialogs.Credentials(); if (credential != null) await ConnectAsync(credential); }, () => !Busy, Failure);
        SearchCommand = new(SearchAsync, () => !Busy && Connected && Folder != null && ValidDates, Failure);
        ExecuteCommand = new(ExecuteAsync, () => ExecutionDisabledReason.Length == 0, Failure);
        StopCommand = new(() => { cancellation?.Cancel(); Status = "중단 요청 · 전송한 변경 요청은 응답 후 멈춥니다"; }, () => Busy && cancellation?.IsCancellationRequested == false);
        SelectAllCommand = new(() => SetSelection(true), () => !Busy && session.Matches(Criteria));
        ClearSelectionCommand = new(() => SetSelection(false), () => !Busy && Mails.Count > 0);
    }
    MailFolder? folder;
    public MailFolder? Folder { get => folder; set { if (Set(ref folder, value)) Invalidate(); } }
    string keyword = "";
    public string Keyword { get => keyword; set { if (Set(ref keyword, value)) Invalidate(); } }
    DateTime? start, end;
    public DateTime? Start { get => start; set { if (Set(ref start, value)) Invalidate(); } }
    public DateTime? End { get => end; set { if (Set(ref end, value)) Invalidate(); } }
    ModeOption mode;
    public ModeOption Mode
    {
        get => mode;
        set
        {
            if (value == null || value == mode) return;
            var before = Criteria;
            if (!Set(ref mode, value)) return;
            if (value.Value == CleanupMode.EmptyDeleted && query != null)
            {
                previousCriteria = before;
                Folder = Folders.FirstOrDefault(x => x.Id == query.DeletedFolderId);
                Keyword = "";
                SelectedSort = SortOptions.Single(x => x.Value == MailSort.DateAsc);
            }
            else if (before.Mode == CleanupMode.EmptyDeleted && previousCriteria is {} previous)
            {
                Folder = Folders.FirstOrDefault(x => x.Id == previous.FolderId);
                Keyword = previous.Keyword; Start = previous.Start; End = previous.End;
                previousCriteria = null;
                SelectedSort = SortOptions[0];
            }
            Notify(nameof(SelectedMode)); Invalidate();
        }
    }
    public CleanupMode SelectedMode { get => Mode.Value; set => Mode = Modes.Single(x => x.Value == value); }
    public bool CanChooseFolder => Idle && Connected && SelectedMode != CleanupMode.EmptyDeleted;
    public bool CanEmptyDeleted => Idle && Connected && !string.IsNullOrEmpty(query?.DeletedFolderId);
    public string ExecuteLabel => SelectedMode == CleanupMode.EmptyDeleted ? "선택 항목 비우기" : "선택 항목 실행";
    public string SearchHint => SelectedMode == CleanupMode.EmptyDeleted ? "지운 편지함 바로 아래의 일반 메일만 조회합니다. 기간을 직접 변경할 수 있습니다." : "선택한 한 폴더만 검색합니다. 종료일의 23:59:59까지 포함됩니다.";
    public string ResultCountSummary => $"검색 결과: {Mails.Count:N0}건 · {MailDisplay.Total(Mails)}";
    public string EmptyMessage => Busy ? "조회 중입니다…" : session.Available ? "조건에 맞는 메일이 없습니다." : "검색 조건을 입력해 주세요.";
    bool busy, allowChanges, reportVisible, reportExpanded;
    bool changingSelection;
    bool dateInputValid = true;
    bool ValidDates => dateInputValid && (Start is null || End is null || Start <= End);
    public void DateTextEdited(bool valid)
    {
        dateInputValid = valid;
        Invalidate();
    }
    public bool Busy { get => busy; private set { Set(ref busy, value); Notify(nameof(Idle)); Refresh(); } }
    public bool Idle => !Busy;
    public bool Connected => transport?.Authenticated == true;
    public bool AllowChanges
    {
        get => allowChanges;
        set
        {
            if (!Set(ref allowChanges, value)) return;
            if (transport != null) transport.AllowChanges = value && Connected;
            settings = settings with { AllowChanges = value };
            try { settingsService.Save(settings); } catch { Status = "변경 허용 설정 저장 실패 · 현재 실행에만 적용됩니다"; }
            Refresh();
        }
    }
    public bool CanChangePermission => Connected && !Busy;
    public bool ReportVisible { get => reportVisible; private set => Set(ref reportVisible, value); }
    public bool ReportExpanded { get => reportExpanded; set => Set(ref reportExpanded, value); }
    string status = "현재 Windows 계정으로 연결을 누르세요 · 기본 처리 방식: 조회만";
    public string Status { get => status; private set => Set(ref status, value); }
    string summary = "";
    public string ResultSummary { get => summary; private set => Set(ref summary, value); }
    double percent;
    public double Percent { get => percent; private set => Set(ref percent, value); }
    bool indeterminate;
    public bool Indeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public string SelectionSummary
    {
        get
        {
            var selected = Mails.Where(x => x.Selected).ToArray();
            return $"선택: {selected.Length:N0}건 · {MailDisplay.Total(selected)}";
        }
    }
    SearchCriteria Criteria => new(Folder?.Id ?? "", Keyword.Trim(), Start?.Date, End?.Date, Mode.Value);
    public string ExecutionDisabledReason => Busy ? "작업이 진행 중입니다" : !Connected ? "EWS 연결이 필요합니다" : !AllowChanges ? "변경 작업 허용이 꺼져 있습니다" :
        Mode.Value == CleanupMode.Inspect ? "조회만 모드입니다. 처리 방식 선택 → 검색 → 대상 선택 순서로 진행하세요" : !ValidDates ? "날짜 범위를 확인하세요" :
        !session.Matches(Criteria) ? "검색 조건 또는 처리 방식이 바뀌었습니다. 다시 검색하세요" :
        Mode.Value == CleanupMode.EmptyDeleted && Folder?.Id != query?.DeletedFolderId ? "휴지통 정리는 지운 편지함에서만 가능합니다" :
        !Mails.Any(x => x.Selected) ? "처리할 메일을 체크하세요" : "";
    void Invalidate()
    {
        session.Invalidate();
        SetSelection(false);
        Refresh();
    }
    void Refresh()
    {
        Notify(nameof(Connected)); Notify(nameof(CanChangePermission)); Notify(nameof(ExecutionDisabledReason)); Notify(nameof(SelectionSummary));
        Notify(nameof(CanChooseFolder)); Notify(nameof(CanEmptyDeleted)); Notify(nameof(ExecuteLabel)); Notify(nameof(SearchHint)); Notify(nameof(ResultCountSummary)); Notify(nameof(EmptyMessage));
        ConnectCommand?.Refresh(); LoginCommand?.Refresh(); SearchCommand?.Refresh(); ExecuteCommand?.Refresh(); StopCommand?.Refresh(); SelectAllCommand?.Refresh(); ClearSelectionCommand?.Refresh();
    }
    void SelectionChanged(object? sender, PropertyChangedEventArgs e) { if (!changingSelection && e.PropertyName == nameof(MailItem.Selected)) Refresh(); }
    void SetSelection(bool selected)
    {
        changingSelection = true;
        try { foreach (var mail in Mails) mail.Selected = selected; }
        finally { changingSelection = false; Refresh(); }
    }
    void ClearMails() { foreach (var mail in Mails) mail.PropertyChanged -= SelectionChanged; Mails.Clear(); }
    void AuthLost()
    {
        allowChanges = false; Notify(nameof(AllowChanges)); Invalidate(); Status = "인증 만료 또는 거부 · 변경 작업 차단 · 다시 연결하세요";
    }
    public async Task ConnectAsync(NetworkCredential? credentials)
    {
        if (Busy) throw new EwsException("작업이 진행 중입니다");
        Begin(); session.Invalidate(); ClearMails(); Folders.Clear(); Folder = null;
        if (transport != null) { transport.AuthenticationLost -= AuthLost; transport.Dispose(); }
        transport = createTransport(credentials); transport.AuthenticationLost += AuthLost;
        query = new(transport); mutations = new(query, new EwsMailWriter(transport));
        allowChanges = false; Notify(nameof(AllowChanges));
        try
        {
            Status = "EWS 통합 인증 및 폴더 조회 중";
            var folders = await query.ConnectAsync(cancellation!.Token);
            foreach (var f in folders) Folders.Add(f);
            string initialFolder = SelectedMode == CleanupMode.EmptyDeleted ? query.DeletedFolderId : query.InboxFolderId;
            Folder = Folders.FirstOrDefault(x => x.Id == initialFolder) ?? Folders.FirstOrDefault();
            allowChanges = settings.AllowChanges ?? true; transport.AllowChanges = allowChanges; Notify(nameof(AllowChanges));
            Status = $"EWS 연결 성공 · 폴더 {Folders.Count:N0}개 · { (allowChanges ? "변경 작업 허용" : "조회 전용") }";
        }
        catch { transport.AllowChanges = false; throw; }
        finally { EndWork(); }
    }
    public async Task SearchAsync()
    {
        if (Busy || !Connected || Folder is null || !ValidDates) throw new EwsException("연결·폴더·검색 기간을 확인하세요");
        var criteria = Criteria; Begin(); ReportVisible = false; ReportExpanded = false; Results.Clear(); session.Invalidate(); ClearMails();
        try
        {
            Status = "메일 검색 중 · 날짜는 한국시간 기준";
            var found = await query!.SearchAsync(criteria, new Progress<WorkProgress>(UpdateProgress), cancellation!.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (criteria != Criteria) return;
            foreach (var mail in found) { mail.PropertyChanged += SelectionChanged; Mails.Add(mail); }
            session.Complete(criteria, found); Status = $"검색 완료 · {found.Count:N0}건 · 본인 사서함과 폴더를 확인하세요";
        }
        finally { EndWork(); }
    }
    public async Task ExecuteAsync()
    {
        if (ExecutionDisabledReason.Length != 0) return;
        var criteria = Criteria; var selected = Mails.Where(x => x.Selected).ToArray();
        if (!dialogs.Confirm($"폴더: {Folder?.Path}\n작업: {Mode.Label}\n대상: {selected.Length:N0}건 · {selected.Sum(x => x.Size ?? 0) / 1048576d:N2} MB\n\n선택한 대상에 실행하시겠습니까?\n첨부 삭제는 취소할 수 없고 휴지통 정리는 서버 보존 정책을 따릅니다.")) return;
        Begin(); ReportVisible = false; Results.Clear(); ReportExpanded = false;
        try
        {
            var results = await mutations!.ExecuteAsync(session, criteria, selected, query!.DeletedFolderId, new Progress<WorkProgress>(UpdateProgress), cancellation!.Token);
            foreach (var result in results) Results.Add(result);
            ResultSummary = string.Join(" · ", Enum.GetValues<ResultKind>().Select(kind => $"{new ProcessingResult("", kind, "").Label} {results.Count(x => x.Kind == kind)}"));
            ReportVisible = true; Status = ResultSummary + " · 다시 검색하여 서버 상태를 확인하세요";
        }
        finally { Invalidate(); ClearMails(); EndWork(); }
    }
    void Begin() { cancellation?.Dispose(); cancellation = new(); Busy = true; Percent = 0; Indeterminate = true; }
    void EndWork() { Busy = false; Indeterminate = false; Percent = 0; cancellation?.Dispose(); cancellation = null; Refresh(); }
    void UpdateProgress(WorkProgress p) { if (!Busy) return; Status = p.Status; Indeterminate = p.Total <= 0; Percent = p.Total > 0 ? Math.Clamp(p.Completed * 100d / p.Total, 0, 100) : 0; }
    void Failure(Exception ex)
    {
        Status = ex switch { OperationCanceledException => "요청을 중단했습니다. 실행하려면 다시 검색하세요", EwsException e => e.Code, HttpRequestException => "회사 EWS 네트워크·TLS 연결 실패 · 사내망/VPN을 확인하세요", _ => $"작업 실패 ({ex.GetType().Name})" };
        Refresh();
    }
    public void Dispose() { cancellation?.Cancel(); cancellation?.Dispose(); transport?.Dispose(); }
}
