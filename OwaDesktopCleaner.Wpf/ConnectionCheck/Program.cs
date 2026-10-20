using System.Net;
using System.Text;
using OwaDesktopCleaner.Wpf.ConnectionCheck;

if (args.SequenceEqual(new[] { "--self-test" })) return await SelfTests.RunAsync();
if (args.Length != 1 || args[0] is not ("--anonymous" or "--windows" or "--credentials"))
{
    Console.WriteLine("읽기 전용 EWS 검증: --anonymous (인증 방식 확인), --windows (현재 Windows 계정), --credentials (직접 입력), --self-test");
    return 2;
}
if (!OperatingSystem.IsWindows() && args[0] != "--anonymous")
{
    Console.WriteLine("회사 Windows PC에서 실행하세요. 기존 프로필/자격 증명은 읽지 않습니다.");
    return 2;
}
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
if (args[0] == "--windows") handler.UseDefaultCredentials = true;
if (args[0] == "--credentials")
{
    Console.Write("EWS 사용자 이름 (회사에서 안내한 형식): ");
    string user = Console.ReadLine()?.Trim() ?? "";
    Console.Write("비밀번호 (표시/저장하지 않음): ");
    var secret = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return 2; }
        if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
        else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
    }
    Console.WriteLine();
    if (user.Length == 0 || secret.Length == 0) return 2;
    var credentials = new NetworkCredential(user, secret.ToString());
    secret.Clear();
    var cache = new CredentialCache();
    foreach (string scheme in new[] { "Negotiate", "NTLM", "Digest", "Basic" })
        cache.Add(EwsReadClient.Endpoint, scheme, credentials);
    handler.Credentials = cache; // Scoped to the exact HTTPS endpoint. No preauthentication.
}
using var http = new HttpClient(handler);
try
{
    var client = new EwsReadClient(http);
    var folder = await client.CallAsync(EwsReadClient.GetFolder(), cancel.Token);
    string id = (string)folder.Element(EwsReadClient.M + "Folders")!.Element(EwsReadClient.T + "Folder")!
        .Element(EwsReadClient.T + "FolderId")!.Attribute("Id")!;
    Console.WriteLine("GetFolder: SOAP NoError · 받은편지함 ID 확인 (값 출력 안 함)");
    var folders = await client.CallAsync(EwsReadClient.FindFolder(), cancel.Token);
    Console.WriteLine($"FindFolder: SOAP NoError · 첫 페이지 {folders.Element(EwsReadClient.M + "RootFolder")!.Element(EwsReadClient.T + "Folders")!.Elements().Count()}건 (최대 5)");
    Console.Write("제목 검색어 (빈칸: 조건 없이 최대 5건 ID만 조회): ");
    string keyword = Console.ReadLine() ?? "";
    var items = await client.CallAsync(EwsReadClient.FindItem(id, keyword), cancel.Token);
    Console.WriteLine($"FindItem: SOAP NoError · {items.Element(EwsReadClient.M + "RootFolder")!.Element(EwsReadClient.T + "Items")!.Elements().Count()}건 (최대 5)");
    Console.WriteLine("C# HttpClient 폴더/검색 최소 검증 성공. WPF UI와 변경 기능은 아직 검증하지 않았습니다.");
    return 0;
}
catch (CheckException ex) { Console.WriteLine(ex.Message); return 1; }
catch (OperationCanceledException) { Console.WriteLine("중단 또는 제한시간 초과"); return 1; }
catch (HttpRequestException ex) { Console.WriteLine($"NETWORK_OR_TLS_ERROR: {ex.HttpRequestError} · {ex.GetBaseException().GetType().Name}"); if (ex.GetBaseException() is System.Net.Sockets.SocketException socket) Console.WriteLine($"Socket: {socket.SocketErrorCode}"); return 1; }
// HttpClient/handler are disposed by using. Credentials cannot be changed after sending.

