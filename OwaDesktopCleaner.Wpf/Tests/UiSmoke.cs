using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MailCleaner.Models;
using MailCleaner.Services;
using MailCleaner.ViewModels;
using MailCleaner.Views;
using static MailCleaner.Services.EwsTransport;

// Exercises the real WPF templates and bindings against an in-process fake server.
// Does not create AuthenticationService, access credentials or use a network handler.
internal static class UiSmoke
{
    public static int Run(string output)
    {
        int exit = 1;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    Directory.CreateDirectory(output);
                    using var vm = new MainViewModel(new Dialogs(), new MemorySettings(), _ => new(new HttpClient(new MockEws())));
                    var window = new MainWindow(_ => vm);
                    window.Show(); await Settle(window);
                    Capture(window, output, "wpf-initial.png");
                    await vm.ConnectAsync(null); await vm.SearchAsync(); await Settle(window);
                    Require(vm.Connected && vm.Mails.Count == 3 && vm.AllowChanges, "mock connection/search");
                    var radio = Children<RadioButton>(window).Single(x => (string)x.Content == "일반 첨부파일 삭제");
                    radio.IsChecked = true;
                    Require(vm.SelectedMode == CleanupMode.RemoveAttachments && !vm.ExecuteCommand.CanExecute(null), "mode binding invalidates search");
                    await vm.SearchAsync(); await Settle(window);
                    var sort = Children<ComboBox>(window).Single(x => ReferenceEquals(x.ItemsSource, vm.SortOptions));
                    sort.SelectedItem = vm.SortOptions.Single(x => x.Value == MailSort.SizeDesc);
                    await Settle(window);
                    var view = (ListCollectionView)CollectionViewSource.GetDefaultView(vm.Mails);
                    Require(((MailItem)view.GetItemAt(0)).Id == "large", "sort control binding");
                    vm.Mails[0].Selected = true;
                    vm.Mails[1].Selected = true;
                    await Settle(window); Capture(window, output, "wpf-results.png");
                    var picker = (DatePicker)window.FindName("StartPicker");
                    picker.IsDropDownOpen = true; await Settle(window);
                    Require(picker.IsDropDownOpen, "date popup template");
                    picker.IsDropDownOpen = false;
                    vm.SelectedMode = CleanupMode.MarkRead; await vm.SearchAsync();
                    vm.Mails[0].Selected = true; await vm.ExecuteAsync(); window.UpdateLayout();
                    Require(vm.ReportVisible && !vm.ReportExpanded, "collapsed report");
                    vm.ReportExpanded = true; await Settle(window); Capture(window, output, "wpf-report.png");
                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    await Settle(window); Capture(window, output, "wpf-minimum.png");
                    await vm.SearchAsync(); Require(!vm.ReportVisible, "new search hides report");
                    window.Close();
                    File.WriteAllText(Path.Combine(output, "ui-checks.txt"), "PASS: real Window, theme, radio binding, dropdown sort, date popup, checkbox selection, mock processing report, minimum-size layout and report reset. Mock HTTP only; no server/credential access.");
                    Console.WriteLine("PASS real WPF UI checks; mock HTTP only"); exit = 0;
                }
                catch (Exception e) { Console.WriteLine(e); }
                finally { app.Shutdown(exit); }
            }));
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }
    static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    static void Capture(Window window, string output, string name)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right), (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
    static async Task Settle(Window window)
    {
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    static void Require(bool condition, string name) { if (!condition) throw new Exception("UI check failed: " + name); }
    sealed class MemorySettings : IUserSettingsStore
    { public UserSettings Load() => new(); public void Save(UserSettings settings) { } }
    sealed class Dialogs : IUserDialogs
    { public bool Confirm(string message) => true; public NetworkCredential? Credentials() => null; }
    sealed class MockEws : HttpMessageHandler
    {
        static XElement Mail(string id, bool attachments)
        {
            bool large = id == "large";
            return new(T + "Message", new XElement(T + "ItemId", new XAttribute("Id", id), new XAttribute("ChangeKey", "mock-key")),
                new XElement(T + "ParentFolderId", new XAttribute("Id", "inbox")), new XElement(T + "ItemClass", "IPM.Note"),
                new XElement(T + "Subject", large ? "[예시] 주간 업무 보고 및 첨부 자료" : id == "small" ? "[예시] 개발팀 일보" : "[예시] 프로젝트 일정 안내"),
                new XElement(T + "From", new XElement(T + "Mailbox", new XElement(T + "Name", large ? "업무지원팀" : "개발팀"), new XElement(T + "EmailAddress", "sample@example.invalid"))),
                new XElement(T + "DateTimeReceived", large ? "2026-10-20T00:30:00Z" : "2026-10-19T08:20:00Z"),
                new XElement(T + "Size", large ? 3565158 : 153600), new XElement(T + "IsRead", id == "small" ? "true" : "false"), new XElement(T + "HasAttachments", "true"),
                attachments ? new XElement(T + "Attachments", new XElement(T + "FileAttachment", new XElement(T + "AttachmentId", new XAttribute("Id", "att-" + id)),
                    new XElement(T + "Name", large ? "주간보고서.xlsx" : "업무일보.pdf"), new XElement(T + "Size", large ? 2621440 : 102400), new XElement(T + "IsInline", "false"))) : null);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var doc = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var op = doc.Root!.Element(S + "Body")!.Elements().Single(); string name = op.Name.LocalName;
            string folderId = (string?)op.Descendants(T + "DistinguishedFolderId").FirstOrDefault()?.Attribute("Id") == "deleteditems" ? "deleted" : "inbox";
            object? body = name switch
            {
                "GetFolder" => new XElement(M + "Folders", new XElement(T + "Folder", new XElement(T + "FolderId", new XAttribute("Id", folderId)), new XElement(T + "DisplayName", folderId == "inbox" ? "받은 편지함" : "지운 편지함"), new XElement(T + "FolderClass", "IPF.Note"))),
                "FindFolder" => new XElement(M + "RootFolder", new XAttribute("IncludesLastItemInRange", "true"), new XAttribute("TotalItemsInView", 0), new XElement(T + "Folders")),
                "FindItem" => new XElement(M + "RootFolder", new XAttribute("IncludesLastItemInRange", "true"), new XAttribute("TotalItemsInView", 3), new XElement(T + "Items", Mail("small", false), Mail("large", false), Mail("other", false))),
                "GetItem" => new XElement(M + "Items", Mail((string)op.Descendants(T + "ItemId").First().Attribute("Id")!, true)),
                "UpdateItem" => null, // Synthetic acknowledgment, no network or mailbox.
                _ => throw new InvalidOperationException("Mock blocks unsupported operation")
            };
            var response = new XElement(S + "Envelope", new XElement(S + "Body", new XElement(M + (name + "Response"), new XElement(M + "ResponseMessages", new XElement(M + (name + "ResponseMessage"),
                new XAttribute("ResponseClass", "Success"), new XElement(M + "ResponseCode", "NoError"), body)))));
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToString(), Encoding.UTF8, "text/xml") };
        }
    }
}
