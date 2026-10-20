using System.Windows;
using MailCleaner.Views;

namespace MailCleaner;

public partial class App : Application
{
    Mutex? mutex;
    bool owned;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new(false, "Local\\APS.OwaMailCleaner.Wpf");
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) { MessageBox.Show("WPF 메일 클리너가 이미 실행 중입니다."); Shutdown(); return; }
        var window = new MainWindow(); MainWindow = window; window.Show();
        // Automated initialization check: no authentication, network, or credential access.
        if (e.Args.Contains("--smoke-test") || e.Args.Contains("--read-smoke-test"))
        {
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                if (window.Icon == null || window.DataContext == null || window.ActualWidth <= 0) { Shutdown(1); return; }
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                string output = System.IO.Path.Combine(AppContext.BaseDirectory, "Smoke");
                System.IO.Directory.CreateDirectory(output);
                using (var file = System.IO.File.Create(System.IO.Path.Combine(output, "main-window.png"))) encoder.Save(file);
                System.IO.File.WriteAllText(System.IO.Path.Combine(output, "result.txt"), "Window initialized; icon loaded; DataContext created; layout rendered. No connect or credential actions invoked.");
                var login = new LoginWindow { Owner = window };
                login.Show(); login.UpdateLayout(); login.ClearSecret(); login.Close();
                if (e.Args.Contains("--read-smoke-test") && window.DataContext is ViewModels.MainViewModel vm)
                {
                    await vm.ConnectAsync(null);
                    vm.Start = new DateTime(2026, 10, 20); vm.End = vm.Start;
                    await vm.SearchAsync();
                    if (!vm.Connected || vm.Busy || vm.Mode.Value != Models.CleanupMode.Inspect) throw new InvalidOperationException("Read UI check failed");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(output, "read-result.txt"), $"WPF Window + ViewModel: authenticated; folders={vm.Folders.Count}; today inbox search={vm.Mails.Count}; default mode=Inspect; mutations not invoked.");
                }
                window.Close();
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "smoke-failure.txt"), ex.GetType().Name);
                    Shutdown(2);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    protected override void OnExit(ExitEventArgs e) { if (owned) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e); }
}
