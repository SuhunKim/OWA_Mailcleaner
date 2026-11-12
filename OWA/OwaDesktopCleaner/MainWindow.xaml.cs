namespace OwaDesktopCleaner;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

public partial class MainWindow : Window
{
    const string Origin = "https://mail.apsfamily.com";
    static readonly HashSet<string> Reads = ["GetFolder", "FindFolder", "FindItem", "GetItem"];
    static readonly HashSet<string> Writes = ["UpdateItem", "MoveItem", "DeleteAttachment", "DeleteItem"];
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwaDesktopCleaner");
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Mutex singleInstance = new(false, "Local\\APS.OwaDesktopCleaner");
    bool authDialogOpen;
    DateTime? authStarted;
    bool ownsMutex, loaded, transitioning, closing, polling, processing, writeRequest, internalNavigation;
    int generation, challenges;
    string profile = "";
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_,_) => {
            try { ownsMutex = singleInstance.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if(!ownsMutex) { MessageBox.Show("메일 클리너가 이미 실행 중입니다."); closing=true; Close(); return; }
            await Initialize(false);
        };
        timer.Tick += async (_,_) => { UpdateAuthProgress(); await Poll(); };
        Closing += OnClosing;
        Closed += (_,_) => { if(ownsMutex) singleInstance.ReleaseMutex(); singleInstance.Dispose(); };
    }
    static bool Trusted(string? value) => Uri.TryCreate(value,UriKind.Absolute,out var u) && u.Scheme=="https" && u.Host=="mail.apsfamily.com" && u.Port==443 && string.IsNullOrEmpty(u.UserInfo);
    static bool Ews(string value) => Trusted(value) && new Uri(value).AbsolutePath.Equals("/EWS/Exchange.asmx",StringComparison.OrdinalIgnoreCase);
    static bool Owa(string? value) => Trusted(value) && (new Uri(value!).AbsolutePath.Equals("/owa",StringComparison.OrdinalIgnoreCase) || new Uri(value!).AbsolutePath.StartsWith("/owa/",StringComparison.OrdinalIgnoreCase));
    void Reset()
    {
        authStarted=null; AuthProgress.Visibility=Visibility.Collapsed;
        generation++; loaded=false; processing=false; writeRequest=false; challenges=0;
        EnableWrites.IsChecked=false; EnableWrites.IsEnabled=false;
        State.Text="OWA 로그인 확인 필요 · EWS 미확인";
    }
    void Controls(bool ready)
    {
        ready=ready && !authDialogOpen;
        CredentialsButton.IsEnabled=ready;
        LoginButton.IsEnabled=ready; OpenButton.IsEnabled=ready; ClearButton.IsEnabled=ready;
        EnableWrites.IsEnabled=ready && loaded;
    }
    async Task Initialize(bool fresh)
    {
        transitioning=true; Controls(false);
        try {
            Directory.CreateDirectory(root);
            string marker=Path.Combine(root,"remember-profile");
            string saved=!fresh && File.Exists(marker)?File.ReadAllText(marker).Trim():"";
            bool reuse=Regex.IsMatch(saved,"^[a-f0-9]{32}$");
            Remember.IsChecked=!fresh && (!File.Exists(Path.Combine(root,"owa-remember")) || File.ReadAllText(Path.Combine(root,"owa-remember"))=="true");
            profile=reuse?saved:Guid.NewGuid().ToString("N");
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"profiles",profile));
            await Browser.EnsureCoreWebView2Async(env);
            var c=Browser.CoreWebView2;
            c.Settings.AreDevToolsEnabled=false; c.Settings.AreHostObjectsAllowed=false; c.Settings.IsWebMessageEnabled=false;
            c.Settings.IsPasswordAutosaveEnabled=Remember.IsChecked==true; c.Settings.IsGeneralAutofillEnabled=false; c.Settings.AreDefaultContextMenusEnabled=false;
            c.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            c.DownloadStarting+=(_,e)=>e.Cancel=true;
            c.NewWindowRequested+=(_,e)=>{e.Handled=true;Notice.Text="새 창을 차단했습니다. 회사 인증에 필요한 경우 출처 확인이 필요합니다.";};
            c.NavigationStarting+=(_,e)=>{
                if(internalNavigation && e.Uri=="about:blank") return;
                if(!Trusted(e.Uri)){e.Cancel=true;Notice.Text="회사 출처 밖의 탐색을 차단했습니다.";return;}
                Reset();
            };
            c.NavigationCompleted+=(_,e)=>{
                if(internalNavigation)return;
                State.Text=e.IsSuccess?"OWA 페이지 로드 완료 · 로그인 후 클리너 열기를 누르세요.":"OWA 연결 실패: "+e.WebErrorStatus;
            };
            c.AddWebResourceRequestedFilter(Origin+"/EWS/Exchange.asmx*",CoreWebView2WebResourceContext.All);
            c.WebResourceRequested+=(_,e)=>{
                if(!loaded || !Ews(e.Request.Uri)) return;
                string action=e.Request.Headers.Contains("SOAPAction")?e.Request.Headers.GetHeader("SOAPAction").Trim('"').Split('/').Last():"";
                writeRequest=Writes.Contains(action);
                challenges=0;
                if((!Reads.Contains(action)&&!Writes.Contains(action)) || (writeRequest && EnableWrites.IsChecked!=true))
                    e.Response=c.Environment.CreateWebResourceResponse(new MemoryStream(),403,"Blocked by desktop operation policy","Content-Type: text/plain");
            };
            c.BasicAuthenticationRequested+=(_,e)=>Authenticate(c,e);
            c.ProcessFailed+=(_,e)=>{Reset();Notice.Text="WebView2 프로세스 오류: "+e.ProcessFailedKind+" · 앱을 다시 실행하세요.";};
            Notice.Text="WebView2 "+env.BrowserVersionString+" · EWS 연결 후 변경 작업 기본 허용 · 전용 프로필 사용";
            c.Navigate(Origin+"/owa/"); timer.Start();
        } catch(Exception ex){State.Text="초기화 실패: "+ex.GetType().Name;Notice.Text=".NET 10 Desktop Runtime 및 WebView2 Runtime 설치 상태를 확인하세요.";}
        finally {transitioning=false;Controls(Browser.CoreWebView2!=null);}
    }
    async void Authenticate(CoreWebView2 core, CoreWebView2BasicAuthenticationRequestedEventArgs args)
    {
        args.Cancel=true;
        if(authDialogOpen || !loaded || !Owa(core.Source) || !Ews(args.Uri) || writeRequest || ++challenges>2) {
            Notice.Text="추가 인증을 중단했습니다. 결과 확인 후 재로그인하세요. 변경 요청은 자동 재시도하지 않습니다.";
            return;
        }
        var deferral=args.GetDeferral();
        int g=generation;
        authDialogOpen=true; authStarted=DateTime.UtcNow; UpdateAuthProgress(); Controls(false);
        EwsLoginWindow? dialog=null;
        try {
            // Leave the WebView2 callback before opening a modal dialog.
            await Dispatcher.InvokeAsync(() => {
                if(g!=generation || !loaded || !Owa(core.Source) || writeRequest)return;
                dialog=new EwsLoginWindow(retry:challenges>1) { Owner=this };
                if(dialog.ShowDialog()==true && g==generation && loaded && Owa(core.Source) && !writeRequest) {
                    args.Response.UserName=dialog.UserName;
                    args.Response.Password=dialog.Password;
                    args.Cancel=false; authStarted=DateTime.UtcNow;
                    Notice.Text="입력한 계정으로 EWS 인증을 진행합니다. 서버 조회 결과로 성공 여부를 확인합니다.";
                } else { authStarted=null; Notice.Text="EWS 로그인을 취소했습니다. 재로그인 후 다시 시도할 수 있습니다."; }
            },DispatcherPriority.Background);
        } catch { authStarted=null; try { args.Cancel=true; } catch {} Notice.Text="EWS 인증 창 처리에 실패했습니다. 재로그인 후 다시 시도하세요."; }
        finally {
            dialog?.ClearSecret(); authDialogOpen=false;
            // Complete exactly once: Dispose also calls Complete in this SDK.
            try { deferral.Complete(); }
            catch { authStarted=null; Notice.Text="EWS 인증 요청이 종료되었거나 만료되었습니다. 재로그인해 주세요."; }
            Controls(!processing); UpdateAuthProgress();
        }
    }
    void UpdateAuthProgress()
    {
        AuthProgress.Visibility=authStarted!=null?Visibility.Visible:Visibility.Collapsed;
        if(authStarted is {} started) {
            int seconds=(int)(DateTime.UtcNow-started).TotalSeconds;
            State.Text=authDialogOpen?"EWS 로그인 입력 대기 중":$"EWS 로그인 중 · 서버 응답 대기 {seconds}초 (최대 약 3분)";
        }
    }
    void RememberOwa(object sender,RoutedEventArgs e)
    {
        try {
            Directory.CreateDirectory(root);
            bool enabled=Remember.IsChecked==true;
            File.WriteAllText(Path.Combine(root,"owa-remember"),enabled?"true":"false");
            if(Browser.CoreWebView2 is {} core)core.Settings.IsPasswordAutosaveEnabled=enabled;
            Notice.Text=enabled?"OWA 로그인 후 WebView2의 비밀번호 저장 제안을 허용하세요. 정상 종료 시 전용 프로필을 보존합니다.":"OWA 저장 제안을 껐습니다. 기존 저장 정보까지 지우려면 로그아웃 · 세션 정리를 누르세요.";
        } catch { Notice.Text="OWA 기억 설정을 저장하지 못했습니다."; }
    }
    async void ManageCredentials(object sender,RoutedEventArgs e)
    {
        try {
            if(!await Idle())return;
            authDialogOpen=true; Controls(false);
            var dialog=new EwsLoginWindow(manage:true) { Owner=this };
            try { dialog.ShowDialog(); } finally { dialog.ClearSecret(); }
            Notice.Text="저장 계정 설정은 다음 EWS 인증 요청에 적용됩니다. 세션 정리와 저장 정보 삭제는 별개입니다.";
        } catch { Notice.Text="EWS 로그인 정보 창을 열지 못했습니다."; }
        finally { authDialogOpen=false; Controls(!processing); }
    }
    async Task<JsonElement?> GetState()
    {
        if(!loaded || !Owa(Browser.CoreWebView2?.Source))return null;
        string raw=await Browser.ExecuteScriptAsync("window.owaDesktop?.status() ?? null");
        using var d=JsonDocument.Parse(raw);
        return d.RootElement.ValueKind==JsonValueKind.Object?d.RootElement.Clone():null;
    }
    async Task Poll()
    {
        if(authDialogOpen||polling||transitioning||closing||!loaded)return;
        polling=true;
        try {
            int g=generation; var s=await GetState(); if(s is null||g!=generation)return;
            processing=s.Value.GetProperty("busy").GetBoolean();
            bool auth=s.Value.GetProperty("authRequired").GetBoolean();
            bool connected=s.Value.GetProperty("connected").GetBoolean();
            EnableWrites.IsChecked=connected && !auth && s.Value.GetProperty("writeEnabled").GetBoolean();
            if(auth || connected || !processing) { authStarted=null; UpdateAuthProgress(); }
            Controls(!processing);
            EnableWrites.IsEnabled=!processing && connected && !auth;
            if(authStarted!=null) { UpdateAuthProgress(); return; }
            if(auth){EnableWrites.IsChecked=false;State.Text="EWS 인증 만료/거부 · 결과 확인 후 재로그인 필요";}
            else State.Text=connected?(processing?"EWS 연결됨 · 작업 진행 중":"EWS 연결됨 · "+(EnableWrites.IsChecked==true?"변경 작업 허용":"조회 전용")):"EWS 연결 확인 중 · 인증 창을 확인하세요.";
        } catch {State.Text="앱 상태를 읽지 못했습니다. 진행 중인 작업 결과를 확인하세요.";}
        finally {polling=false;}
    }
    async Task<bool> Idle()
    {
        if(transitioning||authDialogOpen)return false;
        var state=await GetState();
        if(state?.GetProperty("busy").GetBoolean()==true){Notice.Text="진행 중입니다. 클리너의 중단 요청 후 작업이 끝날 때까지 기다려 주세요.";return false;}
        return true;
    }
    async void OpenCleaner(object sender,RoutedEventArgs e)
    {
        if(transitioning || Browser.CoreWebView2==null)return;
        try {
            if(loaded)return;
            if(!Owa(Browser.CoreWebView2.Source)){Notice.Text="회사 OWA에서 먼저 로그인하세요.";return;}
            int g=generation;
            var ok=await Browser.ExecuteScriptAsync("!location.pathname.toLowerCase().includes('/auth/') && !document.querySelector('input[type=password]')");
            if(ok!="true"||g!=generation){Notice.Text="OWA 로그인을 먼저 완료하세요.";return;}
            loaded=true;challenges=0;EnableWrites.IsChecked=false;Controls(false);
            await Browser.ExecuteScriptAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"cleaner.js")));
            await Poll();
        }catch(Exception ex){Reset();Notice.Text="클리너 열기 실패: "+ex.GetType().Name;Controls(true);}
    }
    async void ChangeWritePermission(object sender,RoutedEventArgs e)
    {
        try {
            if(!await Idle() || !loaded){EnableWrites.IsChecked=false;return;}
            bool requested=EnableWrites.IsChecked==true;
            if(requested && MessageBox.Show("검색 결과가 본인의 의도한 사서함인지 확인했습니까?\n허용 후에도 각 실행마다 대상과 작업을 다시 확인합니다.","변경 작업 허용",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)requested=false;
            string result=await Browser.ExecuteScriptAsync("window.owaDesktop?.setWriteEnabled("+(requested?"true":"false")+") === true");
            EnableWrites.IsChecked=result=="true" && requested;
        }catch{EnableWrites.IsChecked=false;Notice.Text="권한 상태 확인 실패. 재로그인 후 다시 시도하세요.";}
    }
    async void Login(object sender,RoutedEventArgs e)
    {
        try{if(!await Idle())return; Reset();Browser.CoreWebView2?.Navigate(Origin+"/owa/");}
        catch(Exception ex){Notice.Text="재로그인 실패: "+ex.GetType().Name;}
    }
    async Task ClearProfile()
    {
        timer.Stop(); internalNavigation=true;
        if(Browser.CoreWebView2!=null){Browser.CoreWebView2.Stop();Browser.CoreWebView2.Navigate("about:blank");await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();}
        Browser.Dispose();internalNavigation=false;
    }
    async void ClearSession(object sender,RoutedEventArgs e)
    {
        try {
            if(!await Idle())return;transitioning=true;Controls(false);Reset();
            File.Delete(Path.Combine(root,"remember-profile"));File.WriteAllText(Path.Combine(root,"owa-remember"),"false");Remember.IsChecked=false;
            await ClearProfile();
            BrowserHost.Children.Clear(); Browser=new WebView2();BrowserHost.Children.Add(Browser);
            await Initialize(true);
            Notice.Text="이전 프로필의 사이트 데이터를 정리하고 새 전용 프로필로 전환했습니다. 회사 전체 SSO 로그아웃과는 별개입니다.";
        }catch(Exception ex){Notice.Text="세션 정리 실패: "+ex.GetType().Name;}
        finally{transitioning=false;Controls(Browser.CoreWebView2!=null);}
    }
    async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if(closing)return;e.Cancel=true;
        try{
            if(!await Idle())return;transitioning=true;Controls(false);timer.Stop();
            if(Remember.IsChecked==true){File.WriteAllText(Path.Combine(root,"remember-profile"),profile);Browser.Dispose();}
            else{File.Delete(Path.Combine(root,"remember-profile"));await ClearProfile();}
            closing=true;Close();
        }catch(Exception ex){Notice.Text="종료 처리 실패: "+ex.GetType().Name;transitioning=false;Controls(true);}
    }
}
