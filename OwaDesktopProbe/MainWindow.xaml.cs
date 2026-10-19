namespace OwaDesktopProbe;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

public partial class MainWindow : Window
{
    const string Origin = "https://mail.apsfamily.com";
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwaDesktopProbe");
    bool busy, closing, owaConfirmed, httpAuthenticationRequested;
    int generation, authenticationPrompts;
    public record Folder(string Id, string Name);
    public record Mail(string Subject, string From, string Received, string IsRead, string Size);
    public MainWindow() { InitializeComponent(); Loaded += async (_, _) => await Initialize(); Closing += OnClosing; }
    bool Trusted(string? s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "mail.apsfamily.com" && u.Port == 443;
    void Note(string s) { Evidence.AppendText($"{DateTimeOffset.Now:HH:mm:ss zzz} {s}\n"); Evidence.ScrollToEnd(); }
    void Reset() { generation++; owaConfirmed=false; httpAuthenticationRequested=false; ProbeButton.IsEnabled=false; ItemsButton.IsEnabled=false; Folders.ItemsSource=null; Messages.ItemsSource=null; State.Text="OWA 미확인 | EWS 미확인 | 실제 데이터 미확인"; }
    async Task Initialize()
    {
        try {
            Directory.CreateDirectory(root);
            Remember.IsChecked = File.Exists(Path.Combine(root,"remember"));
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"WebView2"));
            await Browser.EnsureCoreWebView2Async(env);
            var c=Browser.CoreWebView2;
            c.Settings.AreDevToolsEnabled=false; c.Settings.AreHostObjectsAllowed=false;
            c.Settings.IsWebMessageEnabled=false; c.Settings.IsPasswordAutosaveEnabled=false;
            c.Settings.IsGeneralAutofillEnabled=false; c.Settings.AreDefaultContextMenusEnabled=false;
            c.BasicAuthenticationRequested += (_,e)=> {
                httpAuthenticationRequested=true;
                // Leave Response untouched: only WebView2's native prompt handles credentials.
                bool allowed=busy && owaConfirmed && Trusted(c.Source) && Trusted(e.Uri)
                    && Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)
                    && uri.AbsolutePath.Equals("/EWS/Exchange.asmx",StringComparison.OrdinalIgnoreCase)
                    && ++authenticationPrompts<=2;
                e.Cancel=!allowed;
                if(!allowed) { Note("허용 범위 밖 또는 반복된 HTTP 인증 요구 차단"); return; }
                Browser.Height=double.NaN; Browser.IsHitTestVisible=true; Browser.UpdateLayout();
                State.Text="OWA: 로그인 확인 | EWS: 기본 인증 창 입력 대기 (최대 3분)";
                Note("회사 EWS 추가 인증: WebView2 기본 창에 OWA와 같은 계정을 직접 입력하세요. 앱은 인증 값을 읽지 않습니다.");
            };
            c.WebResourceResponseReceived += (_,e)=> {
                if(Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var u) && Trusted(e.Request.Uri) && u.AbsolutePath.Equals("/EWS/Exchange.asmx",StringComparison.OrdinalIgnoreCase))
                    Note("WebView2 EWS 실제 HTTP 응답: "+e.Response.StatusCode);
            };
            c.PermissionRequested += (_,e)=> e.State=CoreWebView2PermissionState.Deny;
            c.DownloadStarting += (_,e)=>e.Cancel=true;
            c.NewWindowRequested += (_,e)=> { e.Handled=true; Note("새 창 차단. 필요한 인증 경로는 별도 확인해야 합니다."); };
            c.NavigationStarting += (_,e)=> {
                Reset();
                if(!Trusted(e.Uri)) { e.Cancel=true; Note("외부 탐색 차단: "+(Uri.TryCreate(e.Uri,UriKind.Absolute,out var u)?u.GetLeftPart(UriPartial.Authority):"알 수 없는 출처")); }
            };
            c.NavigationCompleted += (_,e)=> { if(!e.IsSuccess) Note("OWA 탐색 오류: "+e.WebErrorStatus); else Note("OWA 페이지 로드 완료. 로그인 성공 여부는 아직 미확인."); };
            if(Remember.IsChecked!=true) await c.Profile.ClearBrowsingDataAsync();
            Note("WebView2 Runtime "+env.BrowserVersionString+" / 실제 서버: "+Origin+"/EWS/Exchange.asmx");
            c.Navigate(Origin+"/owa/");
        } catch(Exception e) { Note("초기화 실패: "+e.GetType().Name); }
    }
    void Login(object sender,RoutedEventArgs e) { if(!busy && Browser.CoreWebView2!=null) { Browser.Visibility=Visibility.Visible; Browser.IsHitTestVisible=true; Browser.Height=double.NaN; Browser.CoreWebView2.Navigate(Origin+"/owa/"); } }
    async void ConfirmLogin(object sender,RoutedEventArgs e)
    {
        if(busy || Browser.CoreWebView2==null || !Trusted(Browser.Source?.AbsoluteUri)) return;
        try { var s=await Browser.ExecuteScriptAsync("Boolean(location.pathname.toLowerCase().startsWith('/owa') && !location.pathname.toLowerCase().includes('/auth/') && !document.querySelector('input[type=password]'))");
        if(s!="true") { Note("로그인 화면이거나 OWA 경로가 아닙니다."); return; }
        owaConfirmed=true; State.Text="OWA 로그인 완료: 사용자 확인 (자동 확정 아님) | EWS 미확인 | 실제 데이터 미확인";
        ProbeButton.IsEnabled=true;
        Browser.IsHitTestVisible=false; Browser.Height=1;
        Note("OWA 화면 숨김. EWS 성공은 별도 조회로 판정합니다."); } catch(Exception ex) { Failure(ex); }
    }
    async Task<JsonElement> Run(string mode,string id="")
    {
        if(!Trusted(Browser.Source?.AbsoluteUri)) throw new InvalidOperationException("신뢰 출처 아님");
        httpAuthenticationRequested=false; authenticationPrompts=0; int g=generation;
        string key="__owaProbe_"+Guid.NewGuid().ToString("N");
        var script=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"probe.js"))
            .Replace("__KEY__",JsonSerializer.Serialize(key)).Replace("__MODE__",JsonSerializer.Serialize(mode)).Replace("__ID__",JsonSerializer.Serialize(id));
        await Browser.ExecuteScriptAsync(script);
        try {
            for(int i=0;i<1500;i++) {
                await Task.Delay(200);
                if(g!=generation) throw new InvalidOperationException("페이지 전환으로 조회 취소");
                var raw=await Browser.ExecuteScriptAsync($"JSON.stringify(window[{JsonSerializer.Serialize(key)}] ?? null)");
                var json=JsonSerializer.Deserialize<string>(raw);
                if(json==null || json=="null") continue;
                var result=JsonDocument.Parse(json).RootElement.Clone();
                foreach(var ev in result.GetProperty("evidence").EnumerateArray()) Note(ev.GetString()!);
                if(!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetProperty("error").GetString());
                return result;
            }
            throw new InvalidOperationException("조회 제한시간 초과");
        } finally {
            if(g==generation) {
                await Browser.ExecuteScriptAsync($"delete window[{JsonSerializer.Serialize(key)}]");
                Browser.IsHitTestVisible=false; Browser.Height=1;
            }
        }
    }
    async void Probe(object sender,RoutedEventArgs e)
    {
        if(busy) return; busy=true; ItemsButton.IsEnabled=false; Folders.ItemsSource=null; Messages.ItemsSource=null;
        try {
            var r=await Run("folders");
            var list=r.GetProperty("folders").EnumerateArray().Select(f=>new Folder(f.GetProperty("id").GetString()!,f.GetProperty("name").GetString()!)).ToList();
            Folders.ItemsSource=list; Folders.SelectedIndex=0; ItemsButton.IsEnabled=list.Count>0;
            State.Text=$"OWA: 사용자 확인 | EWS 인증 성공 (GetFolder NoError) | 실제 폴더 {list.Count}개 조회 성공 · 메일 미검증";
        } catch(Exception ex) { Failure(ex); } finally { busy=false; }
    }
    async void Items(object sender,RoutedEventArgs e)
    {
        if(busy || Folders.SelectedItem is not Folder f) return; busy=true; Messages.ItemsSource=null;
        try {
            var r=await Run("items",f.Id);
            var list=r.GetProperty("items").EnumerateArray().Select(m=>new Mail(m.GetProperty("subject").GetString()!,m.GetProperty("from").GetString()!,m.GetProperty("received").GetString()!,m.GetProperty("read").GetString()!,m.GetProperty("size").GetString()!)).ToList();
            Messages.ItemsSource=list;
            State.Text=$"OWA: 사용자 확인 | EWS 인증 성공 | 실제 폴더 조회 성공 | 메타데이터 {list.Count}건 조회 성공"+(list.Count==0?" (빈 폴더: 메일이 있는 폴더에서 추가 확인 필요)":" · 1단계 서버 조회 조건 충족");
        } catch(Exception ex) { Failure(ex); } finally { busy=false; }
    }
    void Failure(Exception ex) {
        var reason=(ex is InvalidOperationException?ex.Message:ex.GetType().Name)
            +(httpAuthenticationRequested?" · 추가 HTTP 인증이 요구된 요청입니다. 취소/인증 실패 여부를 확인하세요.":"");
        State.Text=(owaConfirmed?"OWA: 로그인 사용자 확인":"OWA: 미확인")+" | EWS: 현재 요청 실패 | "+reason;
        Note(State.Text); ItemsButton.IsEnabled=false;
    }
    async void ClearSession(object sender,RoutedEventArgs e)
    {
        if(busy || Browser.CoreWebView2==null) return;
        busy=true;
        try { Reset(); Remember.IsChecked=false; File.Delete(Path.Combine(root,"remember")); await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(); Browser.Visibility=Visibility.Visible; Browser.IsHitTestVisible=true; Browser.Height=double.NaN; Browser.CoreWebView2.Navigate(Origin+"/owa/"); Note("앱 전용 쿠키·캐시·사이트 저장소 정리 완료. 서버 SSO 정책에 따라 재인증이 생략될 수 있습니다."); }
        catch(Exception ex) { Note("세션 정리 실패: "+ex.GetType().Name); } finally { busy=false; }
    }
    async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(closing) return; e.Cancel=true;
        if(busy) { Note("조회 종료 후 닫아 주세요."); return; }
        try {
            if(Remember.IsChecked==true) File.WriteAllText(Path.Combine(root,"remember"),"1");
            else { File.Delete(Path.Combine(root,"remember")); if(Browser.CoreWebView2!=null) await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(); }
            Browser.Dispose(); closing=true; Close();
        } catch(Exception ex) { Note("종료 시 세션 정리 실패: "+ex.GetType().Name+" · 세션 정리 버튼으로 다시 시도하세요."); }
    }
}



