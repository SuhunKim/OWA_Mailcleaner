using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SchMailCleaner;

public sealed class MailRow : INotifyPropertyChanged
{
    private bool m_bSelected;
    public event PropertyChangedEventHandler? PropertyChanged;
    // Checkbox state for the result table; drives the "선택: N건" summary and the run button.
    public bool Selected
    {
        get => m_bSelected;
        set { if (m_bSelected == value) return; m_bSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    }
    public string Id { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ReceivedText { get; set; } = "";
    public string SizeText { get; set; } = "";
    public DateTime Received { get; set; }
    public double SizeMb { get; set; }
    public string Cls { get; set; } = "";
    public string Icon { get; set; } = ""; // status icon file name, collected only to cross-check the class
    public string Status { get; set; } = "";
    public int PageNo { get; set; }
    // Unknown (null) until the site's status icons are interpreted.
    public bool? Unread { get; set; }
    public bool? HasAttach { get; set; }
    public string StateText => Unread switch { true => "안 읽음", false => "읽음", _ => "-" };
    public string AttachText => HasAttach switch { true => "있음", false => "없음", _ => "-" };
    // Result of the last run for this mail; every selected mail ends in exactly one outcome.
    public Outcome Outcome { get; set; } = Outcome.NotProcessed;
    public string Note { get; set; } = "";
    // Identity used to compare the same mail across lists: the site id plus the visible fields. The id alone is not trusted across folders.
    public string Key => string.Join("\u001f", Id, Sender, Subject, ReceivedText, SizeText);

    // Read state from the site's own list display: unread rows carry the bold row class "td_list_bold", read rows have no class.
    // Verified on 2026-11-11: 270 rows with that class = the 270 unread shown in the site's folder menu, 106 classless = the rest of 376.
    // Any other class value stays undeterminable. A class change alone is never treated as success; the final state is read after the site reloads.
    public static bool? InterpretUnread(string rowClass)
    {
        var cls = (rowClass ?? "").Trim();
        if (cls.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("td_list_bold")) return true;
        return cls.Length == 0 ? false : null;
    }
}

// Per-mail result of one run. Sum over all outcomes must equal the number of selected mails.
public enum Outcome
{
    NotProcessed,     // no request was ever sent for it (cancelled / halted before its turn)
    Confirmed,        // final state verified per mail
    AlreadyDone,      // already read: not touched
    Skipped,          // no longer in the source list: not touched
    FailedBeforeSend, // error before the site request; request not sent
    NotApplied,       // request sent, site finished, final state shows it was not applied
    Unconfirmed,      // request may have been sent; result could not be confirmed
}

// In-page script error thrown before the site action was clicked (so no request was sent).
public sealed class SiteScriptError : InvalidOperationException
{
    public SiteScriptError(string message) : base(message) { }
}

// The only site dialog the current action may accept: kind, wording and any count it states must match the action.
public sealed record DialogExpectation(Regex Pattern, int Count, bool AllowAlert)
{
    public bool Accepts(CoreWebView2ScriptDialogKind kind, string message)
    {
        if (kind != CoreWebView2ScriptDialogKind.Confirm && !(AllowAlert && kind == CoreWebView2ScriptDialogKind.Alert)) return false;
        if (!Pattern.IsMatch(message)) return false;
        foreach (Match m in Regex.Matches(message, @"(\d[\d,]*)\s*(건|개|통)"))
            if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), out var n) || n != Count) return false;
        return true;
    }
}

public sealed class MailPage
{
    public string Account { get; set; } = "";
    public string Folder { get; set; } = "";
    public int Page { get; set; }
    public int Total { get; set; }
    public int Count { get; set; }
    public List<MailRow> Rows { get; set; } = new();
}

public sealed record FolderItem(string Name, string MenuId)
{
    public override string ToString() => Name;
}

// Hosts the site's browser. It is shown only for login (or on demand) and hidden afterwards, so the main window is just the work panel.
public sealed class BrowserWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public bool AllowClose;
    public BrowserWindow(WebView2 browser)
    {
        Title = "SCH 그룹웨어 로그인";
        // Sized to the site's login card (about 375 x 500) so no extra grey margin is left around it.
        Width = 440; Height = 590; MinWidth = 420; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterOwner; Content = browser;
        Background = System.Windows.Media.Brushes.White;
        // Hide() must not run inside Closing; defer it. The user closing this window only hides it.
        Closing += (s, e) => { if (!AllowClose) { e.Cancel = true; Dispatcher.BeginInvoke(new Action(() => HideSafely())); } };
    }
    // Returns an error text when WPF could not hide the window (then the native hide is used as a fallback).
    public string HideSafely()
    {
        var error = "";
        try { Hide(); } catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && IsVisible) { ShowWindow(handle, 0); error += " (native hide used)"; }
        return error.Trim();
    }
}

public sealed class MainWindow : Window
{
    private const string LoginUrl = "https://hg.sch.ac.kr/index.jsp";
    // SCH groupware brand blue, sampled from the site's login button/logo (#26539C); layout follows the OWA cleaner panel.
    private static readonly System.Windows.Media.Brush s_oPrimary = Brush("#26539C");
    private static System.Windows.Media.Brush Brush(string hex) => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
    // Standard folders reachable through the site's own folder links (ids from the captured page structure).
    private static readonly FolderItem[] s_oFolders =
    {
        new("받은 편지함", "menu_01"), new("내게 쓴 편지함", "menu_10"), new("보낸 편지함", "menu_02"), new("지운 편지함", "menu_03"),
    };

    private readonly WebView2 m_oBrowser = new();
    private readonly BrowserWindow m_oBrowserWindow;
    private readonly DispatcherTimer m_oTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DataGrid m_oGrid = new() { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Brush("#EDF0F3"), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.White, RowBackground = System.Windows.Media.Brushes.White, CanUserAddRows = false };
    private readonly TextBlock m_oStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border m_oStatusBox = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0) };
    private readonly ProgressBar m_oProgress = new() { Height = 6, Margin = new Thickness(0, 8, 0, 0), Foreground = s_oPrimary, Background = Brush("#DBE3F0"), BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
    private readonly TextBlock m_oEmpty = new() { Text = "검색 조건을 입력해 주세요.", Foreground = Brush("#667085"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock m_oCount = new() { FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Text = "검색 결과: -" };
    private readonly TextBlock m_oSummary = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#475467"), Text = "선택: 0건" };
    private readonly TextBlock m_oHint = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#93370D"), Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock m_oLoginState = new() { Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock m_oLoginInfo = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#475467"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border m_oLoginCard = new() { Background = System.Windows.Media.Brushes.White, BorderBrush = Brush("#DFE4EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(32), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel m_oWorkPanel = new() { Margin = new Thickness(18, 14, 18, 14), LastChildFill = true, Visibility = Visibility.Collapsed };
    // Default period matches the existing cleaner: the last 30 days (today-29 .. today), keyword "일보".
    private readonly ComboBox m_oFolder = new() { Width = 170, ItemsSource = s_oFolders, SelectedIndex = 0 };
    private readonly TextBox m_oSubject = new() { Width = 190, MaxLength = 120, ToolTip = "제목에 포함된 글자(일부). 대소문자 구분 없음." };
    private readonly TextBox m_oSender = new() { Width = 150, ToolTip = "보낸사람 이름 전체 또는 일부. 비워 두면 전체 발신자." };
    private readonly DatePicker m_oAfter = new() { Width = 135, SelectedDate = DateTime.Today.AddDays(-29) };
    private readonly DatePicker m_oBefore = new() { Width = 135, SelectedDate = DateTime.Today };
    private readonly TextBox m_oMinimum = new() { Text = "0", Width = 70, ToolTip = "메일 전체 용량 기준(첨부만의 용량이 아님). 0이면 제한 없음." };
    private readonly CheckBox m_oUnreadOnly = new() { Content = "안 읽은 메일만", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), ToolTip = "목록의 상태 표시가 '안 읽음'인 메일만 결과에 담습니다. 상태를 판별하지 못한 메일은 제외됩니다." };
    private readonly Button m_oScan = new() { Content = "대상 메일 검색", IsEnabled = false };
    private readonly Button m_oCancel = new() { Content = "중단 요청", Visibility = Visibility.Collapsed };
    private readonly Button m_oSelectAll = new() { Content = "전체 선택", IsEnabled = false };
    private readonly Button m_oSelectNone = new() { Content = "선택 해제", IsEnabled = false };
    private readonly Button m_oRun = new() { Content = "선택 항목 실행", IsEnabled = false };
    private readonly Button m_oLoginOpen = new() { Content = "로그인 창 열기" };
    private readonly CheckBox m_oRemember = new() { Content = "로그인 정보 기억 (비밀번호 저장 제안·자동 채움)", Margin = new Thickness(0, 14, 0, 0), IsChecked = true, ToolTip = "켜면 로그인 창에서 비밀번호 저장을 제안하고, 다음부터 자동으로 채워 줍니다. 저장 정보는 이 앱 전용 브라우저 프로필에 Windows 사용자 계정으로 암호화되어 보관됩니다. 끄면 저장된 비밀번호를 삭제합니다." };
    private static readonly string s_sRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchMailCleaner");
    private static readonly string s_sRememberFile = Path.Combine(s_sRoot, "remember-login");
    private readonly Button m_oShowBrowser = new() { Content = "사이트 화면 보기" };
    private readonly Button m_oLogout = new() { Content = "로그아웃·세션 정리" };
    private readonly Button m_oDump = new() { Content = "화면 구조 복사", IsEnabled = false, Visibility = Visibility.Collapsed, ToolTip = "현재 화면의 버튼·메뉴 이름과 목록 행의 상태 아이콘 구조만 클립보드에 복사합니다. 메일 제목·본문은 포함하지 않습니다." };
    private readonly Button m_oDevTools = new() { Content = "개발자 도구", IsEnabled = false };
    private readonly RadioButton m_oModeAttach = new() { Content = "일반 첨부파일 삭제 (사용 불가)", GroupName = "mode", IsEnabled = false, Margin = new Thickness(0, 0, 18, 0), ToolTip = "사이트에 첨부만 삭제하는 기능이 있는지 아직 확인되지 않아 제공하지 않습니다." };
    private readonly RadioButton m_oModeRead = new() { Content = "읽음 처리", IsChecked = true, GroupName = "mode", Margin = new Thickness(0, 0, 18, 0) };
    private readonly RadioButton m_oModeTrash = new() { Content = "휴지통 이동", GroupName = "mode", Margin = new Thickness(0, 0, 18, 0) };
    private readonly Button m_oClearTrash = new() { Content = "지운 편지함 비우기 (영구 삭제)", IsEnabled = false, ToolTip = "지운 편지함을 앱이 열어 사이트의 '비우기'를 실행합니다. 되돌릴 수 없습니다." };
    private bool m_bActionsReady, m_bLoggedIn, m_bPolling, m_bBusy;
    private int m_nMissing;
    private string m_sAccount = "", m_sSignature = "", m_sDialog = "", m_sUnexpectedDialog = "", m_sLevel = "";
    // Set only while one specific site request is in flight; any other dialog is never accepted.
    private DialogExpectation? m_oExpected;
    private FolderItem m_oSearchFolder = s_oFolders[0];
    private readonly List<MailRow> m_oRows = new();
    private List<MailRow> m_oFiltered = new();
    private CancellationTokenSource? m_oCancellation;
    private readonly string m_sReadScript;

    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "Local\\APS.SchMailCleaner", out var created);
        if (!created) { MessageBox.Show("순천향 메일클리너가 이미 실행 중입니다."); return; }
        new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }.Run(new MainWindow());
    }

    public MainWindow()
    {
        Title = "순천향 메일클리너";
        Width = 1280; Height = 900; MinWidth = 1000; MinHeight = 700; Background = Brush("#F5F7FA");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SchMailCleaner.ReadMailbox.js")
            ?? throw new InvalidOperationException("조회 스크립트가 없습니다.");
        using var reader = new StreamReader(stream);
        m_sReadScript = reader.ReadToEnd();
        m_oBrowserWindow = new BrowserWindow(m_oBrowser);
        ApplyButtonStyles();

        // Header (SCH blue): title, login state, site view / logout / diagnostics
        var header = new DockPanel { Background = s_oPrimary };
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        headerRight.Children.Add(m_oLoginState); headerRight.Children.Add(m_oShowBrowser); headerRight.Children.Add(m_oLogout); headerRight.Children.Add(m_oDump); headerRight.Children.Add(m_oDevTools);
        DockPanel.SetDock(headerRight, Dock.Right); header.Children.Add(headerRight);
        header.Children.Add(new TextBlock { Text = "순천향 메일클리너", Foreground = System.Windows.Media.Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(18, 12, 0, 12), VerticalAlignment = VerticalAlignment.Center });

        // Login card (shown until the site login is detected)
        var cardPanel = new StackPanel();
        cardPanel.Children.Add(new TextBlock { Text = "SCH 그룹웨어 로그인이 필요합니다", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = s_oPrimary });
        cardPanel.Children.Add(new TextBlock { Text = "로그인 창에서 로그인하면 이 화면이 자동으로 작업 화면으로 바뀝니다. 이 앱은 아이디와 비밀번호를 직접 읽거나 전송하지 않으며, 저장은 브라우저의 비밀번호 저장 기능이 맡습니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16), Foreground = Brush("#344054") });
        cardPanel.Children.Add(m_oLoginOpen); cardPanel.Children.Add(m_oRemember); cardPanel.Children.Add(m_oLoginInfo);
        m_oRemember.IsChecked = !File.Exists(s_sRememberFile) || File.ReadAllText(s_sRememberFile).Trim() != "false";
        m_oRemember.Click += UiClick_Remember;
        m_oLoginCard.Child = cardPanel;

        // Work panel (same order as the existing cleaner): conditions → mode → search → status → results → footer
        var form = new WrapPanel();
        UIElement Field(string label, FrameworkElement input)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
            box.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold, Foreground = Brush("#344054"), Margin = new Thickness(0, 0, 0, 4) });
            box.Children.Add(input); return box;
        }
        form.Children.Add(Field("폴더", m_oFolder));
        form.Children.Add(Field("제목 키워드", m_oSubject));
        form.Children.Add(Field("보낸사람 이름", m_oSender));
        form.Children.Add(Field("시작일", m_oAfter));
        form.Children.Add(Field("종료일", m_oBefore));
        form.Children.Add(Field("최소 용량 (MB)", m_oMinimum));

        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        modes.Children.Add(new TextBlock { Text = "처리 방식", FontWeight = FontWeights.Bold, Foreground = Brush("#344054"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        modes.Children.Add(m_oModeAttach); modes.Children.Add(m_oModeRead); modes.Children.Add(m_oModeTrash);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(m_oScan); actions.Children.Add(m_oCancel); actions.Children.Add(m_oUnreadOnly);
        actions.Children.Add(new TextBlock { Text = "선택한 한 폴더만 검색합니다. 종료일의 23:59:59까지 포함됩니다. 제목 키워드와 보낸사람을 비우면 기간 안의 전체 메일을 보여 줍니다.", Foreground = Brush("#667085"), VerticalAlignment = VerticalAlignment.Center });
        m_oStatusBox.Child = m_oStatus;

        var head = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var headBtns = new StackPanel { Orientation = Orientation.Horizontal }; headBtns.Children.Add(m_oSelectAll); headBtns.Children.Add(m_oSelectNone);
        DockPanel.SetDock(headBtns, Dock.Right); head.Children.Add(headBtns); head.Children.Add(m_oCount);

        var tableHost = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        tableHost.Children.Add(m_oGrid); tableHost.Children.Add(m_oEmpty);
        var tableBorder = new Border { BorderBrush = Brush("#DFE4EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = tableHost, ClipToBounds = true, Background = System.Windows.Media.Brushes.White };

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var footerRight = new StackPanel { Orientation = Orientation.Horizontal };
        footerRight.Children.Add(m_oHint); footerRight.Children.Add(m_oRun);
        DockPanel.SetDock(footerRight, Dock.Right); footer.Children.Add(footerRight); footer.Children.Add(m_oSummary);

        var danger = new Border { BorderBrush = Brush("#F4C7C3"), Background = Brush("#FEF3F2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 0) };
        var dangerRow = new DockPanel(); DockPanel.SetDock(m_oClearTrash, Dock.Right); dangerRow.Children.Add(m_oClearTrash);
        dangerRow.Children.Add(new TextBlock { Text = "지운 편지함 영구 삭제: 앱이 지운 편지함을 열어 사이트의 '비우기'를 실행합니다. 되돌릴 수 없습니다.", Foreground = Brush("#B42318"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        danger.Child = dangerRow;

        void Dock_(UIElement e) { DockPanel.SetDock(e, Dock.Top); m_oWorkPanel.Children.Add(e); }
        Dock_(form); Dock_(modes); Dock_(actions); Dock_(m_oStatusBox); Dock_(m_oProgress); Dock_(head);
        DockPanel.SetDock(danger, Dock.Bottom); m_oWorkPanel.Children.Add(danger);
        DockPanel.SetDock(footer, Dock.Bottom); m_oWorkPanel.Children.Add(footer);
        m_oWorkPanel.Children.Add(tableBorder);

        var body = new Grid(); body.Children.Add(m_oWorkPanel); body.Children.Add(m_oLoginCard);
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header); layout.Children.Add(body); Content = layout;

        // Result table: checkbox + received / sender / subject / state / attachment / whole-mail size
        var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
        checkFactory.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding("Selected") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        checkFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        checkFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        m_oGrid.Columns.Add(new DataGridTemplateColumn { Header = "선택", Width = 52, CellTemplate = new DataTemplate { VisualTree = checkFactory } });
        foreach (var column in new[] { ("수신일", "ReceivedText", 130d), ("발신자", "Sender", 150d), ("제목", "Subject", 0d), ("상태", "StateText", 70d), ("첨부", "AttachText", 60d), ("메일 전체 크기", "SizeText", 110d) })
            m_oGrid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new System.Windows.Data.Binding(column.Item2), Width = column.Item3 == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(column.Item3) });
        m_oGrid.ColumnHeaderStyle = (Style)System.Windows.Markup.XamlReader.Parse("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{x:Type DataGridColumnHeader}' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Setter Property='Background' Value='#F7F9FC'/><Setter Property='Foreground' Value='#475467'/><Setter Property='FontWeight' Value='Bold'/><Setter Property='Padding' Value='10,8'/><Setter Property='BorderBrush' Value='#EDF0F3'/><Setter Property='BorderThickness' Value='0,0,0,1'/></Style>");

        m_oSelectAll.Click += (s, ev) => { foreach (var r in m_oFiltered) r.Selected = true; };
        m_oSelectNone.Click += (s, ev) => { foreach (var r in m_oFiltered) r.Selected = false; };
        m_oRun.Click += async (s, ev) => await RunActionAsync(m_oModeRead.IsChecked == true);
        m_oClearTrash.Click += UiClick_ClearTrash;
        m_oDump.Click += UiClick_Dump;
        // The button is hidden; diagnostics stay reachable with Ctrl+Shift+D when the site layout changes again.
        PreviewKeyDown += (s, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.D && System.Windows.Input.Keyboard.Modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) && m_oDump.IsEnabled)
            { ev.Handled = true; UiClick_Dump(m_oDump, new RoutedEventArgs()); }
        };
        m_oDevTools.Click += (s, ev) => m_oBrowser.CoreWebView2?.OpenDevToolsWindow();
        m_oLoginOpen.Click += (s, ev) => ShowBrowser();
        m_oShowBrowser.Click += (s, ev) => ShowBrowser();
        m_oLogout.Click += UiClick_Logout;
        m_oScan.Click += async (s, ev) => await SearchAsync();
        m_oCancel.Click += (s, ev) => { m_oCancellation?.Cancel(); m_oStatus.Text = "현재 요청이 끝난 뒤 중단합니다."; };
        foreach (var box in new[] { m_oSubject, m_oSender, m_oMinimum })
        {
            box.KeyDown += (s, ev) => { if (ev.Key == System.Windows.Input.Key.Enter && m_oScan.IsEnabled) { ev.Handled = true; _ = SearchAsync(); } };
            box.TextChanged += (s, ev) => InvalidateResults();
        }
        m_oAfter.SelectedDateChanged += (s, ev) => InvalidateResults();
        m_oBefore.SelectedDateChanged += (s, ev) => InvalidateResults();
        m_oFolder.SelectionChanged += (s, ev) => InvalidateResults();
        m_oUnreadOnly.Click += (s, ev) => InvalidateResults();
        foreach (var radio in new[] { m_oModeRead, m_oModeTrash }) radio.Checked += (s, ev) => InvalidateResults();
        // Colour the status box by severity whenever the text changes (info / success / warning / error).
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(m_oStatus, (s, ev) => ColourStatus());
        m_oTimer.Tick += OnTimerTick;
        m_oStatus.Text = "브라우저를 준비하는 중입니다.";
        m_oLoginState.Text = "● 로그인 필요";
        m_oShowBrowser.IsEnabled = false; m_oLogout.IsEnabled = false; m_oLoginOpen.IsEnabled = false;
        RefreshRunState();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    // Severity colours follow the OWA cleaner panel (info/success/warning/error); wording decides the level.
    // Run results carry an explicit level because their wording always contains words like "실패 0건".
    private void SetStatus(string text, string level = "")
    {
        m_sLevel = level; m_oStatus.Text = text; m_sLevel = ""; // the change handler runs synchronously
    }

    private void ColourStatus()
    {
        var text = m_oStatus.Text;
        string back, fore;
        if (m_sLevel == "error") { back = "#FEF3F2"; fore = "#B42318"; }
        else if (m_sLevel == "warn") { back = "#FFFAEB"; fore = "#93370D"; }
        else if (m_sLevel == "ok") { back = "#ECFDF3"; fore = "#067647"; }
        else if (Regex.IsMatch(text, "실패|오류|중단: |차단")) { back = "#FEF3F2"; fore = "#B42318"; }
        else if (Regex.IsMatch(text, "미확인|중단|건너뜀|다릅니다|비어 있습니다|변경되었습니다|만료|입력|늦습니다|없습니다")) { back = "#FFFAEB"; fore = "#93370D"; }
        else if (Regex.IsMatch(text, "찾았습니다|완료|확인:|반영 확인")) { back = "#ECFDF3"; fore = "#067647"; }
        else { back = "#E8EDF6"; fore = "#1E3F7A"; }
        m_oStatusBox.Background = Brush(back); m_oStatus.Foreground = Brush(fore);
    }

    // One style per button kind (SCH blue primary / outline / red danger); disabled state is dimmed.
    private void ApplyButtonStyles()
    {
        Style Make(string back, string fore, string border, string hover) => (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Button}'>" +
            "<Setter Property='Background' Value='" + back + "'/><Setter Property='Foreground' Value='" + fore + "'/><Setter Property='BorderBrush' Value='" + border + "'/>" +
            "<Setter Property='Padding' Value='14,8'/><Setter Property='Margin' Value='0,0,8,0'/><Setter Property='FontWeight' Value='SemiBold'/><Setter Property='Cursor' Value='Hand'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'><Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='" + hover + "'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        var outline = Make("#FFFFFF", "#26539C", "#9FB3D3", "#EEF2F9");
        var primary = Make("#26539C", "#FFFFFF", "#26539C", "#1E4380");
        var danger = Make("#D92D20", "#FFFFFF", "#D92D20", "#B42318");
        foreach (var b in new[] { m_oCancel, m_oSelectAll, m_oSelectNone }) b.Style = outline;
        foreach (var b in new[] { m_oScan, m_oRun, m_oLoginOpen }) b.Style = primary;
        m_oClearTrash.Style = danger;
        var headerStyle = Make("#1FFFFFFF", "#FFFFFF", "#66FFFFFF", "#38FFFFFF");
        foreach (var b in new[] { m_oShowBrowser, m_oLogout, m_oDump, m_oDevTools }) b.Style = headerStyle;
    }

    // Single place that decides which action buttons are usable and why not (shown next to the run button).
    private void RefreshRunState()
    {
        var selected = m_oFiltered.Count(r => r.Selected);
        m_oSummary.Text = string.Format("선택: {0}건", selected);
        var current = m_sSignature.Length > 0 && m_sSignature == Signature();
        var hasList = m_bActionsReady && m_oFiltered.Count > 0;
        m_oSelectAll.IsEnabled = hasList && !m_bBusy; m_oSelectNone.IsEnabled = hasList && !m_bBusy;
        m_oRun.IsEnabled = hasList && !m_bBusy && selected > 0 && current;
        m_oHint.Text = m_bBusy ? "작업 진행 중입니다."
            : !m_bLoggedIn ? "먼저 로그인하세요."
            : !m_bActionsReady ? "먼저 대상 메일을 검색하세요."
            : !current ? "검색 조건이 바뀌었습니다. 다시 검색하세요."
            : m_oFiltered.Count == 0 ? "조건에 맞는 메일이 없어 실행할 수 없습니다."
            : selected == 0 ? "실행할 메일을 체크하세요." : "";
    }

    // Everything that defines "what was searched"; run is only allowed while the controls still match the last search.
    private string Signature() => string.Join("|", ((FolderItem)m_oFolder.SelectedItem).Name, m_oSubject.Text.Trim(), m_oSender.Text.Trim(),
        m_oAfter.SelectedDate?.ToString("yyyy-MM-dd"), m_oBefore.SelectedDate?.ToString("yyyy-MM-dd"), m_oMinimum.Text.Trim(), m_oModeRead.IsChecked == true ? "read" : "trash", m_oUnreadOnly.IsChecked == true ? "unread" : "all");

    private void InvalidateResults()
    {
        if (m_bBusy || m_sSignature.Length == 0 || !IsLoaded) return;
        if (m_sSignature != Signature()) { m_oStatus.Text = "검색 조건이 변경되었습니다. 다시 검색해 주세요."; }
        RefreshRunState();
    }

    private void SetBusyUi(bool busy)
    {
        m_bBusy = busy;
        m_oScan.IsEnabled = !busy && m_bLoggedIn; m_oClearTrash.IsEnabled = !busy && m_bLoggedIn;
        m_oCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; m_oCancel.IsEnabled = busy;
        foreach (var c in new Control[] { m_oFolder, m_oSubject, m_oSender, m_oMinimum, m_oAfter, m_oBefore, m_oModeRead, m_oModeTrash, m_oUnreadOnly, m_oLogout }) c.IsEnabled = !busy;
        // While the app drives the site, the user must not open or operate the site view (account/folder/selection must not change),
        // and the site's own dialogs must not appear for the user to click through.
        var ready = m_oBrowser.CoreWebView2 is not null;
        m_oShowBrowser.IsEnabled = !busy && ready; m_oLoginOpen.IsEnabled = !busy && ready; m_oDevTools.IsEnabled = !busy && ready; m_oDump.IsEnabled = !busy && ready;
        if (busy && m_oBrowserWindow.IsVisible) m_oBrowserWindow.HideSafely();
        m_oBrowserWindow.IsEnabled = !busy;
        if (m_oBrowser.CoreWebView2 is { } core) core.Settings.AreDefaultScriptDialogsEnabled = !busy;
        RefreshRunState();
    }

    // State-transition log only (no mail data, no credentials) to diagnose login-window show/hide behaviour.
    private static void Log(string text)
    {
        try { Directory.CreateDirectory(s_sRoot); File.AppendAllText(Path.Combine(s_sRoot, "state.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine); }
        catch (Exception) { /* logging must never break the app */ }
    }

    private void ShowBrowser(string reason = "user") { if (m_bBusy && reason == "user") return; Log("ShowBrowser reason=" + reason + " visibleBefore=" + m_oBrowserWindow.IsVisible); m_oBrowserWindow.Show(); m_oBrowserWindow.Activate(); }

    // Keep login data in the application's own local browser profile; the browser lives in its own window.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log("---- app start ----");
        m_oBrowserWindow.Owner = this; m_oBrowserWindow.Show();
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SchMailCleaner", "Browser");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            await m_oBrowser.EnsureCoreWebView2Async(environment);
            var core = m_oBrowser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = m_oRemember.IsChecked == true;
            core.Settings.IsGeneralAutofillEnabled = m_oRemember.IsChecked == true;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (s, ev) => ev.Handled = true;
            // Accept only a dialog that matches the request in flight (kind, wording, stated count). Anything else is never accepted:
            // while busy, default dialogs are disabled so it is dismissed, and it is recorded so the run stops.
            core.ScriptDialogOpening += (s, ev) =>
            {
                var expected = m_oExpected;
                if (expected is not null && expected.Accepts(ev.Kind, ev.Message)) { m_sDialog = ev.Message; ev.Accept(); }
                else if (m_bBusy) m_sUnexpectedDialog += (m_sUnexpectedDialog.Length > 0 ? " / " : "") + ev.Message;
            };
            m_oBrowser.Source = new Uri(LoginUrl);
            m_oTimer.Start();
            m_oDump.IsEnabled = true; m_oDevTools.IsEnabled = true; m_oShowBrowser.IsEnabled = true; m_oLogout.IsEnabled = true; m_oLoginOpen.IsEnabled = true;
            m_oStatus.Text = "로그인 창에서 로그인하세요.";
            m_oLoginInfo.Text = "";
        }
        catch (Exception ex) { m_oStatus.Text = m_oLoginInfo.Text = string.Format("브라우저 초기화 실패: {0}. WebView2 Runtime 설치 여부를 확인하세요.", ex.Message); }
    }

    // Prevent accidental navigation to unrelated destinations.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "hg.sch.ac.kr")
        { e.Cancel = true; m_oStatus.Text = m_oLoginInfo.Text = "다른 사이트로 이동이 차단되었습니다. 외부 SSO가 필요하면 별도 지원이 필요합니다."; }
    }

    // Login detection: the mail frame with the folder tree and the user id appears only after a successful login.
    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (m_bPolling || m_bBusy || m_oBrowser.CoreWebView2 is null) return;
        m_bPolling = true;
        try
        {
            var json = await m_oBrowser.ExecuteScriptAsync("(() => { try { const d = document.querySelector('frame#Start')?.contentDocument; return !!(d && d.querySelector('#menu_01') && d.querySelector('#UID')); } catch (e) { return false; } })()");
            if (json == "true") { m_nMissing = 0; if (!m_bLoggedIn) SetLoggedIn(true, "로그인되었습니다. 조건을 입력하고 대상 메일을 검색하세요."); }
            else if (m_bLoggedIn && ++m_nMissing >= 6) SetLoggedIn(false, "로그인 세션이 끝났습니다. 다시 로그인하세요.");
            else if (m_bLoggedIn && m_nMissing > 0) Log("poll miss " + m_nMissing + " json=" + json);
        }
        catch (Exception ex) { if (m_bLoggedIn || m_oLoginState.Text.Contains("필요")) m_oLoginInfo.Text = "로그인 상태 확인 중 오류: " + ex.GetType().Name + " " + ex.Message; }
        finally { m_bPolling = false; }
    }

    private void SetLoggedIn(bool on, string message)
    {
        Log("SetLoggedIn on=" + on + " msg=" + message + " browserVisibleBefore=" + m_oBrowserWindow.IsVisible);
        m_bLoggedIn = on; m_nMissing = 0;
        m_oWorkPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        m_oLoginCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        m_oLoginState.Text = on ? "● 로그인됨" : "● 로그인 필요";
        m_oStatus.Text = message; m_oLoginInfo.Text = on ? "" : message;
        if (on) { var hideError = m_oBrowserWindow.HideSafely(); Log("HideSafely error='" + hideError + "' visibleAfter=" + m_oBrowserWindow.IsVisible); if (hideError.Length > 0) m_oStatus.Text = message + " (로그인 창 숨김 문제: " + hideError + ")"; }
        else
        {
            m_oRows.Clear(); m_oFiltered = new(); m_oGrid.ItemsSource = null; m_sSignature = ""; m_bActionsReady = false;
            m_oCount.Text = "검색 결과: -"; m_oEmpty.Text = "검색 조건을 입력해 주세요."; m_oEmpty.Visibility = Visibility.Visible;
            ShowBrowser("logged-out");
        }
        m_oScan.IsEnabled = on && !m_bBusy; m_oClearTrash.IsEnabled = on && !m_bBusy;
        RefreshRunState();
    }

    // Remember-login toggle: enables the browser's own password save/autofill; turning it off also deletes saved passwords.
    private async void UiClick_Remember(object sender, RoutedEventArgs e)
    {
        var on = m_oRemember.IsChecked == true;
        try
        {
            Directory.CreateDirectory(s_sRoot);
            File.WriteAllText(s_sRememberFile, on ? "true" : "false");
            var core = m_oBrowser.CoreWebView2;
            if (core is null) return;
            core.Settings.IsPasswordAutosaveEnabled = on; core.Settings.IsGeneralAutofillEnabled = on;
            if (!on) await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill);
            m_oLoginInfo.Text = on ? "로그인 정보 기억을 켰습니다. 다음 로그인부터 비밀번호 저장을 제안합니다." : "로그인 정보 기억을 끄고 저장된 비밀번호를 삭제했습니다.";
        }
        catch (Exception ex) { m_oLoginInfo.Text = "로그인 정보 기억 설정 실패: " + ex.Message; }
    }

    // Clear the session (cookies, site storage, cache) and go back to the login state. Saved passwords are kept unless remember-login is off.

    private async void UiClick_Logout(object sender, RoutedEventArgs e)
    {
        if (m_bBusy || m_oBrowser.CoreWebView2 is null) return;
        if (MessageBox.Show(this, "이 앱의 로그인 세션을 종료합니다. 저장된 비밀번호는 유지됩니다(로그인 정보 기억이 켜진 경우). 계속하시겠습니까?", "로그아웃", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            m_oBrowser.CoreWebView2.Navigate("about:blank");
            await m_oBrowser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
            SetLoggedIn(false, "로그아웃했습니다. 다시 로그인하세요.");
            m_oBrowser.CoreWebView2.Navigate(LoginUrl);
        }
        catch (Exception ex) { m_oStatus.Text = "세션 정리 실패: " + ex.Message; }
    }

    private string Validate()
    {
        if (!m_oAfter.SelectedDate.HasValue || !m_oBefore.SelectedDate.HasValue) return "시작일과 종료일을 확인해 주세요.";
        if (m_oAfter.SelectedDate.Value.Date > m_oBefore.SelectedDate.Value.Date) return "시작일은 종료일보다 늦을 수 없습니다.";
        if (!double.TryParse(m_oMinimum.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum) || !double.IsFinite(minimum) || minimum < 0) return "최소 용량에 0 이상의 숫자를 입력하세요.";
        if (((FolderItem)m_oFolder.SelectedItem).MenuId == "menu_03" && m_oModeTrash.IsChecked == true) return "지운 편지함의 메일은 휴지통으로 이동할 수 없습니다. 읽음 처리를 선택하거나 다른 폴더를 선택하세요.";
        return "";
    }

    // Open the chosen folder with the site's own folder link, then wait until its list is shown.
    private async Task<MailPage> OpenFolderAsync(FolderItem folder, CancellationToken token)
    {
        try { var current = await ReadPageAsync(); if (current.Folder == folder.Name) return current; }
        catch (InvalidOperationException) { /* not on a list yet */ }
        await RunInFrameAsync("const a = d.querySelector('#" + folder.MenuId + "'); if (!a) throw Error('편지함 메뉴를 찾을 수 없습니다.'); a.click(); return true;");
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(600, token);
            try { var page = await ReadPageAsync(); if (page.Folder == folder.Name) return page; }
            catch (InvalidOperationException) { /* still loading */ }
        }
        throw new TimeoutException(string.Format("'{0}'을(를) 열지 못했습니다.", folder.Name));
    }

    // Search = read the chosen folder page by page, then apply the criteria. Partial results are discarded on failure/cancel.
    private async Task SearchAsync(string finalMessage = "", string finalLevel = "")
    {
        if (m_bBusy || !m_bLoggedIn) return;
        var invalid = Validate();
        if (invalid.Length > 0) { m_oStatus.Text = invalid; return; }
        var folder = (FolderItem)m_oFolder.SelectedItem;
        var signature = Signature();
        var subjectQuery = m_oSubject.Text.Trim(); var senderQuery = m_oSender.Text.Trim();
        var after = m_oAfter.SelectedDate!.Value.Date; var before = m_oBefore.SelectedDate!.Value.Date;
        var minimum = double.Parse(m_oMinimum.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
        var markRead = m_oModeRead.IsChecked == true;
        var unreadOnly = m_oUnreadOnly.IsChecked == true;
        SetBusyUi(true);
        m_oRows.Clear(); m_oFiltered = new(); m_oGrid.ItemsSource = null; m_sSignature = ""; m_bActionsReady = false;
        m_oCount.Text = "검색 결과: -"; m_oEmpty.Text = "검색 중입니다…"; m_oEmpty.Visibility = Visibility.Visible;
        m_oProgress.IsIndeterminate = true; m_oProgress.Visibility = Visibility.Visible;
        m_oStatus.Text = "대상 메일을 검색하는 중입니다.";
        m_oCancellation = new CancellationTokenSource();
        try
        {
            var token = m_oCancellation.Token;
            var (initial, collected) = await ScanFolderAsync(folder, token, false, (pageNumber, total, count) =>
            {
                m_oProgress.IsIndeterminate = false; m_oProgress.Maximum = total; m_oProgress.Value = pageNumber;
                m_oStatus.Text = string.Format("메일을 검색하는 중입니다. {0}/{1} 페이지 · {2}건 수집", pageNumber, total, count);
            });
            foreach (var row in collected) { ParseRow(row); row.PropertyChanged += (s, ev) => RefreshRunState(); }
            token.ThrowIfCancellationRequested();
            var filtered = collected.Where(row =>
                (subjectQuery.Length == 0 || row.Subject.Contains(subjectQuery, StringComparison.OrdinalIgnoreCase)) &&
                (senderQuery.Length == 0 || row.Sender.Contains(senderQuery, StringComparison.OrdinalIgnoreCase)) &&
                row.Received.Date >= after && row.Received.Date <= before && row.SizeMb >= minimum &&
                (!unreadOnly || row.Unread == true)).ToList();
            m_sAccount = initial.Account; m_oSearchFolder = folder;
            m_oRows.AddRange(collected); m_oFiltered = filtered;
            // Everything found is listed for review. Read mode only pre-checks mails not known to be read; trash mode pre-checks everything found, like the existing cleaner.
            foreach (var row in filtered) row.Selected = !markRead || row.Unread != false;
            m_oGrid.ItemsSource = filtered; m_bActionsReady = true; m_sSignature = signature;
            m_oCount.Text = string.Format("검색 결과: {0}건", filtered.Count);
            m_oEmpty.Text = "조건에 맞는 메일이 없습니다."; m_oEmpty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var summary = string.Format("{0}건을 찾았습니다(폴더 전체 {1}건 중). 실행하지 않을 메일은 선택을 해제해 주세요. 표시 용량은 메일 전체 용량이며 첨부만의 용량이 아닙니다.", filtered.Count, collected.Count);
            var undetermined = collected.Count(r => r.Unread == null);
            if (markRead) summary += string.Format(" 이 중 안 읽음 {0}건만 처음에 체크되어 있고, 이미 읽은 메일은 체크되어 있지 않습니다.", filtered.Count(r => r.Unread == true));
            if (undetermined > 0)
                summary += " [진단] 상태 표시 분포(아이콘·행 클래스·건수): " + string.Join(", ", collected.GroupBy(r => (r.Icon.Length > 0 ? r.Icon : "없음") + " / '" + r.Cls + "'").OrderByDescending(g => g.Count()).Select(g => g.Key + " ×" + g.Count())) + ".";
            if (undetermined > 0) summary += string.Format(" 읽음 상태를 판별하지 못한 메일 {0}건{1}.", undetermined, unreadOnly ? "은 '안 읽은 메일만' 조건에서 제외했습니다" : "이 있습니다");
            SetStatus(finalMessage.Length > 0 ? finalMessage + " 재검색: " + filtered.Count + "건 남음." : summary, finalLevel);
        }
        catch (OperationCanceledException) { SetStatus((finalMessage + " 검색이 중단되었습니다. 부분 결과는 표시하지 않습니다.").Trim(), finalLevel); m_oEmpty.Text = "검색이 중단되어 표시할 목록이 없습니다."; m_oEmpty.Visibility = Visibility.Visible; }
        catch (Exception ex) { m_oRows.Clear(); SetStatus((finalMessage + " 재검색 실패: " + ex.Message).Trim(), finalMessage.Length > 0 ? (finalLevel == "ok" ? "warn" : finalLevel) : ""); m_oEmpty.Text = "검색에 실패해 표시할 목록이 없습니다."; m_oEmpty.Visibility = Visibility.Visible; }
        finally
        {
            m_oProgress.Visibility = Visibility.Collapsed;
            m_oCancellation?.Dispose(); m_oCancellation = null;
            SetBusyUi(false);
        }
    }

    // Execute the site's pagination and await actual DOM replacement.
    private async Task<MailPage> MoveToPageAsync(int number, MailPage initial, CancellationToken token)
    {
        var current = await ReadPageAsync();
        if (current.Page != number)
        {
            var script = string.Format(CultureInfo.InvariantCulture,
                "(() => {{ const d=document.querySelector('frame#Start').contentDocument; const a=d.querySelector('a[href=\"javascript:gotoPage({0})\"]'); if(!a) throw Error('페이지 링크가 없습니다.'); a.click(); }})()", number - 1);
            await m_oBrowser.ExecuteScriptAsync(script);
        }
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, token);
            current = await ReadPageAsync();
            if (current.Account != initial.Account || current.Folder != initial.Folder || current.Total != initial.Total)
                throw new InvalidOperationException("계정 또는 편지함 구성이 변경되었습니다. 다시 검색하세요.");
            if (current.Page == number && (current.Rows.Count > 0 || initial.Total == 1)) return current;
        }
        throw new TimeoutException("메일 페이지 이동 시간이 초과되었습니다.");
    }

    // Wrap DOM errors explicitly because WebView2 otherwise returns null for JS failures.
    private async Task<MailPage> ReadPageAsync()
    {
        var script = string.Format("(() => {{ try {{ return {{data: {0}}}; }} catch(e) {{ return {{error: e.message}}; }} }})()", m_sReadScript);
        var json = await m_oBrowser.ExecuteScriptAsync(script);
        using var result = JsonDocument.Parse(json);
        if (result.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("메일 화면에 접근하지 못했습니다.");
        if (result.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        return result.RootElement.GetProperty("data").Deserialize<MailPage>() ?? throw new InvalidOperationException("메일 응답이 비어 있습니다.");
    }

    // Reject unknown dates or units rather than silently excluding mail.
    private static void ParseRow(MailRow row)
    {
        row.Received = DateTime.ParseExact(row.ReceivedText, "yyyy.MM.dd HH:mm", CultureInfo.InvariantCulture);
        var match = Regex.Match(row.SizeText, @"^([\d,.]+)\s*(B|KB|MB|GB)$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new FormatException("인식하지 못한 메일 크기 형식입니다.");
        var size = double.Parse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
        row.SizeMb = size * (match.Groups[2].Value.ToUpperInvariant() switch { "B" => 1d / 1048576, "KB" => 1d / 1024, "MB" => 1d, "GB" => 1024d, _ => throw new FormatException() });
        row.Unread = MailRow.InterpretUnread(row.Cls);
    }

    // Reads every page of a folder. With requireComplete the row count must equal the site's own total, otherwise the scan is rejected.
    private async Task<(MailPage Info, List<MailRow> Rows)> ScanFolderAsync(FolderItem folder, CancellationToken token, bool requireComplete, Action<int, int, int>? progress = null)
    {
        var initial = await OpenFolderAsync(folder, token);
        var rows = new List<MailRow>(); var ids = new HashSet<string>();
        for (var pageNumber = 1; pageNumber <= initial.Total; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            var page = await MoveToPageAsync(pageNumber, initial, token);
            foreach (var row in page.Rows)
            {
                if (!ids.Add(row.Id)) throw new InvalidOperationException("페이지 사이에 중복 메일이 발견되었습니다. 다시 검색하세요.");
                row.PageNo = pageNumber; rows.Add(row);
            }
            progress?.Invoke(pageNumber, initial.Total, rows.Count);
        }
        if (requireComplete && rows.Count != initial.Count) throw new InvalidOperationException(string.Format("'{0}' 조회 건수({1})가 사이트 총 건수({2})와 달라 확인에 쓸 수 없습니다.", folder.Name, rows.Count, initial.Count));
        return (initial, rows);
    }

    // Runs a snippet in the site's list frame; returns its value or throws the in-page error message.
    private async Task<JsonElement> RunInFrameAsync(string body)
    {
        var script = "(() => { try { if (location.origin !== 'https://hg.sch.ac.kr') throw Error('허용된 사이트가 아닙니다.'); const f = document.querySelector('frame#Start'); const d = f && f.contentDocument; if (!d) throw Error('메일 화면을 찾을 수 없습니다.'); const w = f.contentWindow; return { ok: (() => { " + body + " })() }; } catch (e) { return { error: e.message }; } })()";
        using var doc = JsonDocument.Parse(await m_oBrowser.ExecuteScriptAsync(script));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("메일 화면에 접근하지 못했습니다.");
        // Thrown by the script itself before any action was clicked; other failures (no result, engine error) stay ambiguous.
        if (doc.RootElement.TryGetProperty("error", out var error)) throw new SiteScriptError(error.GetString() ?? "알 수 없는 화면 오류");
        return doc.RootElement.GetProperty("ok").Clone();
    }

    // Jump with the site's own gotoPage() (same call as its page links), then wait for the target page to render.
    private async Task<MailPage> GoToPageForActionAsync(int number, CancellationToken token)
    {
        var current = await ReadPageAsync();
        if (current.Page != number)
            await RunInFrameAsync("if (typeof w.gotoPage !== 'function') throw Error('페이지 이동 함수를 찾을 수 없습니다.'); w.gotoPage(" + (number - 1).ToString(CultureInfo.InvariantCulture) + "); return true;");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, token);
            try { current = await ReadPageAsync(); } catch (InvalidOperationException) { continue; }
            if (current.Account != m_sAccount || current.Folder != m_oSearchFolder.Name) throw new InvalidOperationException("계정 또는 편지함이 검색 때와 다릅니다. 다시 검색하세요.");
            if (current.Page == number && current.Rows.Count > 0) return current;
        }
        throw new TimeoutException("메일 페이지 이동 시간이 초과되었습니다.");
    }

    private static readonly FolderItem s_oTrashFolder = s_oFolders.First(f => f.MenuId == "menu_03");
    // Wording the site's confirmation/alert must have for each action (matched together with the kind and any stated count).
    private static readonly Regex s_oReadDialog = new("읽");
    private static readonly Regex s_oTrashDialog = new(@"삭제|지운\s*편지함|휴지통|이동");
    private static readonly Regex s_oClearDialog = new(@"비우|영구|삭제|지운\s*편지함");

    private static void Mark(MailRow row, Outcome outcome, string note) { row.Outcome = outcome; row.Note = note; }

    // One script = verify account/folder/selection and click the site's own link, in a single page turn, so nothing can change in between.
    // An in-page error is thrown before the click (SiteScriptError = request not sent).
    private static string ActionScript(IEnumerable<string> ids, string account, string folder, string href) =>
        "const ids = new Set(" + JsonSerializer.Serialize(ids) + "); const acc = " + JsonSerializer.Serialize(account) + "; const fol = " + JsonSerializer.Serialize(folder) + "; const href = " + JsonSerializer.Serialize(href) + ";" +
        "if ((d.querySelector('#UID')?.value || '') !== acc) throw Error('계정이 검색 때와 달라 실행하지 않았습니다.');" +
        "const h = (d.querySelector('.list_content h2') || d.querySelector('h2'))?.textContent.trim() || '';" +
        "if (h !== fol) throw Error('편지함이 검색 때와 달라 실행하지 않았습니다.');" +
        "let n = 0; for (const tr of d.querySelectorAll('tr[id^=\"mail_list-\"]')) { const cb = tr.querySelector('input[type=\"checkbox\"]'); if (!cb) throw Error('메일 선택 칸을 찾지 못했습니다.'); const want = ids.has(cb.dataset.mid); if (cb.checked !== want) cb.click(); if (cb.checked !== want) throw Error('선택 상태를 맞추지 못했습니다.'); if (cb.checked) n++; }" +
        "if (n !== ids.size) throw Error('선택 건수가 일치하지 않아 실행하지 않았습니다.');" +
        "const a = Array.from(d.querySelectorAll('a')).find(x => x.getAttribute('href') === href); if (!a) throw Error('사이트의 실행 버튼을 찾지 못했습니다.');" +
        "d.documentElement.setAttribute('data-sch-mc', '1'); a.click(); return n;";

    // true = the list document is the same one the request was sent from (no reload yet), false = it was reloaded, null = not readable right now.
    private async Task<bool?> MarkerPresentAsync()
    {
        try { return (await RunInFrameAsync("return d.documentElement.getAttribute('data-sch-mc') === '1';")).GetBoolean(); }
        catch (Exception) { return null; }
    }

    private static string OutcomeLabel(Outcome o) => o switch
    {
        Outcome.Confirmed => "반영 확인", Outcome.AlreadyDone => "이미 처리됨", Outcome.Skipped => "건너뜀", Outcome.FailedBeforeSend => "실패(요청 전 오류)",
        Outcome.NotApplied => "실패(처리 후 미반영)", Outcome.Unconfirmed => "결과 미확인", _ => "미처리",
    };

    // Run the chosen action for checked rows via the site's own toolbar link. Pages are processed last→first so removals never shift unprocessed pages.
    // Every selected mail ends in exactly one Outcome; any unclear result stops all later requests and nothing is retried.
    private async Task RunActionAsync(bool markRead)
    {
        if (m_bBusy) return;
        if (m_sSignature.Length == 0 || m_sSignature != Signature()) { m_oStatus.Text = "검색 조건이 변경되었습니다. 다시 검색해 주세요."; return; }
        var targets = m_oFiltered.Where(r => r.Selected).ToList();
        if (targets.Count == 0) { m_oStatus.Text = "실행할 메일을 선택해 주세요."; return; }
        var label = markRead ? "읽음 처리" : "휴지통 이동";
        var ask = string.Format("계정 {0} · {1}\n선택한 {2}건에 '{3}' 작업을 실행할까요?\n\n메일 내용과 대상을 다시 확인해 주세요.", m_sAccount, m_oSearchFolder.Name, targets.Count, label);
        if (markRead) ask += "\n\n이미 읽음으로 표시된 메일은 다시 처리하지 않고 따로 집계합니다.";
        else ask += "\n\n지운 편지함으로 이동하며, 보관 기간이 지나면 사이트가 자동 삭제할 수 있습니다. 이동 결과는 메일별로 확인하며, 확인하지 못하면 '결과 미확인'으로 표시합니다.";
        if (MessageBox.Show(this, ask, "실행 확인", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusyUi(true);
        m_oProgress.IsIndeterminate = false; m_oProgress.Maximum = Math.Max(1, targets.Select(r => r.PageNo).Distinct().Count()); m_oProgress.Value = 0; m_oProgress.Visibility = Visibility.Visible;
        m_oCancellation = new CancellationTokenSource();
        foreach (var t in targets) Mark(t, Outcome.NotProcessed, "");
        m_sDialog = ""; m_sUnexpectedDialog = "";
        var stop = ""; var halt = false;
        var pending = new List<(MailRow Row, bool Settled)>(); // trash moves awaiting the per-mail final check
        var trashBefore = new HashSet<string>();
        try
        {
            var token = m_oCancellation.Token;
            await OpenFolderAsync(m_oSearchFolder, token);
            if (!markRead)
            {
                // Record the trash first, so a mail already there is never mistaken for one that just arrived.
                m_oStatus.Text = "휴지통의 현재 상태를 기록하는 중입니다.";
                var snapshot = await ScanFolderAsync(s_oTrashFolder, token, true);
                if (snapshot.Info.Account != m_sAccount) throw new InvalidOperationException("계정이 검색 때와 다릅니다.");
                trashBefore = snapshot.Rows.Select(r => r.Key).ToHashSet();
                await OpenFolderAsync(m_oSearchFolder, token);
            }
            var groups = targets.GroupBy(r => r.PageNo).OrderByDescending(g => g.Key).ToList();
            for (var gi = 0; gi < groups.Count && !halt; gi++)
            {
                var group = groups[gi];
                if (token.IsCancellationRequested) { stop += " 사용자가 중단했습니다. 요청을 보내지 않은 메일은 미처리로 남깁니다."; break; }
                var sent = false;
                var toSend = new List<MailRow>();
                m_oStatus.Text = string.Format("{0} 중입니다. {1}/{2} 페이지 묶음", label, gi + 1, groups.Count);
                try
                {
                    await OpenFolderAsync(m_oSearchFolder, token);
                    var before = await GoToPageForActionAsync(group.Key, token);
                    foreach (var r in group)
                    {
                        var now = before.Rows.FirstOrDefault(x => x.Id == r.Id);
                        if (now is null) { Mark(r, Outcome.Skipped, "원본 목록에서 이미 사라져 요청하지 않음"); continue; }
                        if (now.Key != r.Key) { Mark(r, Outcome.Skipped, "같은 ID의 메일 내용이 달라져 요청하지 않음"); continue; }
                        if (markRead && MailRow.InterpretUnread(now.Cls) == false) { Mark(r, Outcome.AlreadyDone, "목록 표시가 이미 읽음이라 요청하지 않음"); continue; }
                        toSend.Add(r);
                    }
                    if (toSend.Count == 0) { m_oProgress.Value = gi + 1; continue; }
                    var script = ActionScript(toSend.Select(r => r.Id), m_sAccount, m_oSearchFolder.Name, markRead ? "javascript:checkedSetRead(true);" : "javascript:deleteMessages(false);");
                    try
                    {
                        // Only this action's own dialog may be accepted, until its result is verified. No automatic retry.
                        m_oExpected = new DialogExpectation(markRead ? s_oReadDialog : s_oTrashDialog, toSend.Count, true);
                        sent = true; // from here on the request may have reached the site
                        try { await RunInFrameAsync(script); }
                        catch (SiteScriptError) { sent = false; throw; } // thrown before the click: nothing was sent
                        foreach (var r in toSend) Mark(r, Outcome.Unconfirmed, "요청 후 결과 확인 전");
                        if (markRead)
                        {
                            var deadline = DateTime.UtcNow.AddSeconds(25); var reloaded = false; var lastError = ""; Dictionary<string, MailRow>? after = null;
                            while (DateTime.UtcNow < deadline)
                            {
                                await Task.Delay(800);
                                try
                                {
                                    if (await MarkerPresentAsync() == false) reloaded = true;
                                    var page = await GoToPageForActionAsync(group.Key, CancellationToken.None);
                                    after = page.Rows.ToDictionary(x => x.Id);
                                    var states = toSend.Select(r => after.TryGetValue(r.Id, out var x) && x.Key == r.Key ? MailRow.InterpretUnread(x.Cls) : null).ToList();
                                    if (states.All(s => s == false)) break;
                                    if (reloaded && states.All(s => s.HasValue)) break;
                                }
                                catch (Exception ex) { lastError = ex.Message; }
                            }
                            foreach (var r in toSend)
                            {
                                if (after is null) { Mark(r, Outcome.Unconfirmed, "처리 후 목록을 읽지 못함" + (lastError.Length > 0 ? ": " + lastError : "")); continue; }
                                if (!after.TryGetValue(r.Id, out var x) || x.Key != r.Key) { Mark(r, Outcome.Unconfirmed, "처리 후 목록에서 같은 메일을 찾지 못해 읽음 상태 확인 불가"); continue; }
                                var state = MailRow.InterpretUnread(x.Cls);
                                if (state == false) Mark(r, Outcome.Confirmed, "목록 상태 표시가 읽음");
                                else if (state == null) Mark(r, Outcome.Unconfirmed, "목록 상태 표시를 읽음/안 읽음으로 판별하지 못함(아이콘 구조 해석 필요)");
                                else if (reloaded) Mark(r, Outcome.NotApplied, "사이트 화면이 갱신된 뒤에도 안 읽음으로 표시됨");
                                else Mark(r, Outcome.Unconfirmed, "안 읽음 표시 그대로이며 화면 갱신을 확인하지 못함");
                            }
                        }
                        else
                        {
                            // Gate for the next request only: the site's total must have dropped by exactly this group. Per-mail proof comes from the final scan.
                            var expectedCount = before.Count - toSend.Count; var settled = false; var anomaly = "";
                            var deadline = DateTime.UtcNow.AddSeconds(25);
                            while (DateTime.UtcNow < deadline)
                            {
                                await Task.Delay(800);
                                try
                                {
                                    var cur = await ReadPageAsync();
                                    if (cur.Account != m_sAccount || cur.Folder != m_oSearchFolder.Name) { anomaly = "처리 후 계정 또는 편지함이 바뀌었습니다."; break; }
                                    if (cur.Count == expectedCount)
                                    {
                                        settled = true;
                                        if (cur.Page == group.Key && cur.Rows.Any(x => toSend.Any(t => t.Id == x.Id))) { settled = false; anomaly = "총 건수는 줄었으나 선택한 메일이 목록에 남아 있습니다."; }
                                        break;
                                    }
                                    if (cur.Count < expectedCount) { anomaly = "총 건수가 예상보다 더 줄었습니다."; break; }
                                }
                                catch (Exception) { /* page is reloading */ }
                            }
                            foreach (var r in toSend) { Mark(r, Outcome.Unconfirmed, anomaly.Length > 0 ? anomaly : "사이트 처리 완료를 확인하지 못함"); pending.Add((r, settled)); }
                        }
                    }
                    finally { m_oExpected = null; }
                    if (toSend.Any(r => r.Outcome != Outcome.Confirmed && markRead)) { halt = true; stop += " 결과가 불명확하여 이후 요청을 중단했습니다. 자동 재시도하지 않습니다."; }
                    if (!markRead && pending.Any(p => !p.Settled)) { halt = true; stop += " 처리 결과가 불명확하여 이후 요청을 중단했습니다. 자동 재시도하지 않습니다."; }
                    if (m_sUnexpectedDialog.Length > 0) { halt = true; stop += " 예상하지 못한 사이트 확인창이 있어 수락하지 않고 이후 요청을 중단했습니다."; }
                    m_oProgress.Value = gi + 1;
                }
                catch (OperationCanceledException) when (!sent)
                {
                    stop += " 사용자가 중단했습니다. 요청을 보내지 않은 메일은 미처리로 남깁니다."; halt = true;
                }
                catch (Exception ex)
                {
                    halt = true;
                    if (!sent) { foreach (var r in group.Where(r => r.Outcome == Outcome.NotProcessed)) Mark(r, Outcome.FailedBeforeSend, ex.Message); stop += " 요청 전 오류로 중단했습니다(요청은 보내지 않음): " + ex.Message; }
                    else
                    {
                        foreach (var r in toSend) { Mark(r, Outcome.Unconfirmed, "요청 후 오류: " + ex.Message); if (!markRead && pending.All(p => p.Row != r)) pending.Add((r, false)); }
                        stop += " 요청을 보낸 뒤 오류가 발생해 결과를 확인하지 못했습니다. 이후 요청을 중단했습니다: " + ex.Message;
                    }
                }
            }
            if (pending.Count > 0)
            {
                // Per-mail proof for trash moves: gone from the source folder AND the same mail (same id and visible fields) newly present in the trash.
                m_oStatus.Text = "이동 결과를 메일별로 확인하는 중입니다.";
                try
                {
                    var origin = await ScanFolderAsync(m_oSearchFolder, CancellationToken.None, true);
                    var trash = await ScanFolderAsync(s_oTrashFolder, CancellationToken.None, true);
                    if (origin.Info.Account != m_sAccount || trash.Info.Account != m_sAccount) throw new InvalidOperationException("확인 중 계정이 달라졌습니다.");
                    var originRows = origin.Rows.ToDictionary(r => r.Id); var trashKeys = trash.Rows.Select(r => r.Key).ToHashSet();
                    foreach (var (row, settled) in pending)
                    {
                        if (originRows.TryGetValue(row.Id, out var still))
                        {
                            if (still.Key != row.Key) Mark(row, Outcome.Unconfirmed, "원본 폴더에 같은 ID의 다른 메일이 있어 확인 불가");
                            else if (settled) Mark(row, Outcome.NotApplied, "사이트 처리 후에도 원본 폴더에 남아 있음");
                            else Mark(row, Outcome.Unconfirmed, "원본 폴더에 남아 있으나 사이트 처리 완료를 확인하지 못함");
                        }
                        else if (trashKeys.Contains(row.Key) && !trashBefore.Contains(row.Key)) Mark(row, Outcome.Confirmed, "원본에서 사라지고 휴지통에 같은 ID·내용으로 도착");
                        else Mark(row, Outcome.Unconfirmed, "원본에서는 사라졌으나 휴지통에서 같은 메일(ID·내용)을 확정하지 못함. 폴더 이동 후 ID 유지 근거가 없어 도착은 미확인");
                    }
                }
                catch (Exception ex) { foreach (var (row, _) in pending) Mark(row, Outcome.Unconfirmed, "메일별 최종 확인 조회 실패: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) { stop += " 사용자가 중단했습니다. 요청을 보내지 않은 메일은 미처리로 남깁니다."; }
        catch (Exception ex) { stop += " 시작 전 오류로 요청을 보내지 않고 중단했습니다: " + ex.Message; }
        finally
        {
            m_oExpected = null;
            m_oProgress.Visibility = Visibility.Collapsed;
            m_oCancellation?.Dispose(); m_oCancellation = null;
            SetBusyUi(false);
        }
        // Tally: the six buckets must add up to the number of selected mails.
        int Count(params Outcome[] o) => targets.Count(t => o.Contains(t.Outcome));
        int confirmed = Count(Outcome.Confirmed), already = Count(Outcome.AlreadyDone), skipped = Count(Outcome.Skipped), failedSend = Count(Outcome.FailedBeforeSend), notApplied = Count(Outcome.NotApplied), unconfirmed = Count(Outcome.Unconfirmed), notProcessed = Count(Outcome.NotProcessed);
        var failed = failedSend + notApplied;
        var sum = confirmed + already + skipped + failed + unconfirmed + notProcessed;
        var result = string.Format("{0} {1}: 선택 {2}건 = 반영 확인 {3} + 이미 처리됨 {4} + 건너뜀 {5} + 실패 {6}(요청 전 오류 {7}, 처리 후 미반영 {8}) + 결과 미확인 {9}(요청이 갔을 수 있음) + 미처리 {10}(요청 전 중단).{11}",
            label, halt ? "중단" : "종료", targets.Count, confirmed, already, skipped, failed, failedSend, notApplied, unconfirmed, notProcessed, stop);
        if (sum != targets.Count) result += string.Format(" ※ 집계 불일치(합계 {0}건): 결과를 신뢰하지 마세요.", sum);
        var notes = targets.Where(t => t.Note.Length > 0 && t.Outcome != Outcome.Confirmed).GroupBy(t => OutcomeLabel(t.Outcome) + ": " + t.Note).Take(3).Select(g => "\n· " + g.Key + " (" + g.Count() + "건)");
        result += string.Concat(notes);
        if (m_sUnexpectedDialog.Length > 0) result += "\n· 수락하지 않은 사이트 확인창: " + m_sUnexpectedDialog;
        if (m_sDialog.Length > 0) result += "\n· 수락한 사이트 확인창: " + m_sDialog;
        var level = failed > 0 || sum != targets.Count ? "error" : unconfirmed + notProcessed > 0 || m_sUnexpectedDialog.Length > 0 ? "warn" : "ok";
        // Like the existing cleaner: re-search after the run so the list shows the real remaining state.
        await SearchAsync(result, level);
    }

    // Issue #11: scope is the whole folder shown by the site's own 'empty' link; irreversible, so a separate warning is required.
    // The account/folder/count shown in the confirmation are re-checked in the same page turn that clicks 'empty', and the result is read back from the same account's trash.
    private async void UiClick_ClearTrash(object sender, RoutedEventArgs e)
    {
        if (m_bBusy || !m_bLoggedIn) return;
        SetBusyUi(true);
        var message = ""; var level = "warn"; var sent = false;
        m_sDialog = ""; m_sUnexpectedDialog = "";
        try
        {
            var opened = await OpenFolderAsync(s_oTrashFolder, CancellationToken.None);
            var account = opened.Account; var count = opened.Count;
            if (opened.Folder != s_oTrashFolder.Name) throw new InvalidOperationException("지운 편지함 화면이 아닙니다.");
            if (count == 0) { message = "지운 편지함이 비어 있습니다."; level = ""; return; }
            var ask = string.Format("계정 {0}\n폴더 '{1}'의 {2}건 전체를 영구 삭제합니다.\n되돌릴 수 없습니다.\n\n사이트의 '비우기' 기능을 그대로 실행하며, 처리 범위는 사이트가 정합니다. 실행 직전에 계정·폴더·건수가 위와 같은지 다시 확인하고, 다르면 실행하지 않습니다. 계속하시겠습니까?", account, opened.Folder, count);
            if (MessageBox.Show(this, ask, "영구 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { message = "지운 편지함 비우기를 취소했습니다."; level = ""; return; }
            var script = "const acc = " + JsonSerializer.Serialize(account) + "; const fol = " + JsonSerializer.Serialize(opened.Folder) + "; const cnt = " + count.ToString(CultureInfo.InvariantCulture) + ";" +
                "if ((d.querySelector('#UID')?.value || '') !== acc) throw Error('계정이 확인창에 표시한 계정과 달라 실행하지 않았습니다.');" +
                "const h = (d.querySelector('.list_content h2') || d.querySelector('h2'))?.textContent.trim() || '';" +
                "if (h !== fol) throw Error('폴더가 확인창에 표시한 폴더와 달라 실행하지 않았습니다.');" +
                "if (Number(d.querySelector('#msgTotalCount')?.value) !== cnt) throw Error('건수가 확인창에 표시한 건수와 달라 실행하지 않았습니다.');" +
                "const a = Array.from(d.querySelectorAll('a')).find(x => x.getAttribute('onclick') === 'submitTrashboxClear(false);'); if (!a) throw Error('사이트의 비우기 버튼을 찾지 못했습니다.'); a.click(); return true;";
            var verified = false; var finalCount = -1; var finalAccount = "";
            try
            {
                m_oExpected = new DialogExpectation(s_oClearDialog, count, true);
                sent = true;
                try { await RunInFrameAsync(script); }
                catch (SiteScriptError) { sent = false; throw; }
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline && !verified)
                {
                    await Task.Delay(800);
                    try
                    {
                        var cur = await ReadPageAsync();
                        finalAccount = cur.Account; finalCount = cur.Count;
                        verified = cur.Account == account && cur.Folder == s_oTrashFolder.Name && cur.Count == 0;
                    }
                    catch (Exception) { /* reloading */ }
                }
                if (!verified)
                {
                    try { var cur = await OpenFolderAsync(s_oTrashFolder, CancellationToken.None); finalAccount = cur.Account; finalCount = cur.Count; verified = cur.Account == account && cur.Count == 0; }
                    catch (Exception) { /* keep what was read */ }
                }
            }
            finally { m_oExpected = null; }
            if (verified) { message = string.Format("지운 편지함 비우기 확인: 계정 {0}의 지운 편지함 {1}건 → 0건.", account, count); level = "ok"; }
            else if (finalAccount.Length > 0 && finalAccount != account) message = "지운 편지함 비우기 결과 미확인: 확인 시점의 계정이 실행 때와 달라 같은 계정의 휴지통에서 확인하지 못했습니다. 재시도하지 않았습니다.";
            else if (finalCount >= 0 && finalCount < count) { message = string.Format("지운 편지함 비우기 결과 미확인(일부만 처리된 것으로 보임): 시작 {0}건 → 현재 {1}건. 재시도하지 않았습니다. 사이트 화면에서 직접 확인하세요.", count, finalCount); }
            else message = string.Format("지운 편지함 비우기 결과 미확인(시작 {0}건, 현재 {1}). 사이트가 처리 중이거나 거부했을 수 있습니다. 재시도하지 않았습니다. 사이트 화면에서 직접 확인하세요.", count, finalCount < 0 ? "불명" : finalCount + "건");
        }
        catch (Exception ex)
        {
            message = sent ? "지운 편지함 비우기 결과 미확인(요청이 갔을 수 있음): " + ex.Message + " 재시도하지 않았습니다. 사이트 화면에서 직접 확인하세요."
                : "지운 편지함 비우기 중단: 요청을 보내기 전 오류로 실행하지 않았습니다. " + ex.Message;
            level = sent ? "warn" : "error";
        }
        finally
        {
            m_oExpected = null; m_sSignature = ""; SetBusyUi(false);
            if (m_sUnexpectedDialog.Length > 0) { message += " 수락하지 않은 사이트 확인창: " + m_sUnexpectedDialog; level = level == "ok" ? "warn" : level; }
            if (m_sDialog.Length > 0) message += " 수락한 사이트 확인창: " + m_sDialog;
            SetStatus(message, level);
        }
    }

    // Issue #11: read-only capture of control names/handlers and the status-icon markup of a few list rows (no subjects, senders or values).
    private async void UiClick_Dump(object sender, RoutedEventArgs e)
    {
        const string script = "(() => { try { const out = []; for (const f of ['Main','Start']) { const d = document.querySelector('frame#' + f)?.contentDocument; if (!d) continue; d.querySelectorAll('a,button,input,select,option').forEach(x => { if (x.closest('tr[id^=\"mail_list-\"]')) return; if (x.type === 'password') return; out.push({f, tag: x.tagName, t: (x.textContent || x.value || '').trim().slice(0, 30), href: x.getAttribute('href'), onclick: x.getAttribute('onclick'), id: x.id, name: x.name, type: x.type}); }); } const s = document.querySelector('frame#Start')?.contentDocument; if (s) Array.from(s.querySelectorAll('tr[id^=\"mail_list-\"]')).slice(0, 12).forEach(tr => out.push({f: 'Start', tag: 'ROW', cls: tr.className, cell0: (tr.cells[0]?.innerHTML || '').replace(/\\s+/g, ' ').slice(0, 700)})); return JSON.stringify(out); } catch (ex) { return JSON.stringify({error: ex.message}); } })()";
        try
        {
            var json = JsonSerializer.Deserialize<string>(await m_oBrowser.ExecuteScriptAsync(script)) ?? "";
            Clipboard.SetText(json);
            m_oStatus.Text = string.Format("화면 구조 {0:N0}자를 클립보드에 복사했습니다. 메모장에 붙여넣어 전달하세요.", json.Length);
        }
        catch (Exception ex) { m_oStatus.Text = string.Format("화면 구조 복사 실패: {0}", ex.Message); }
    }

    // Stop pending work and let the hidden browser window close with the app.
    private void OnClosed(object? sender, EventArgs e)
    {
        m_oCancellation?.Cancel(); m_oTimer.Stop();
        m_oBrowserWindow.AllowClose = true; m_oBrowserWindow.Close();
    }
}
