using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MailCleaner.ViewModels;

namespace MailCleaner.Views;

public partial class MainWindow : Window, IUserDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        if (DesignerProperties.GetIsInDesignMode(this)) return;
        DataContext = new MainViewModel(this);
        foreach (var picker in new[] { StartPicker, EndPicker })
        {
            picker.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => CheckDateText()));
            picker.SelectedDateChanged += (_, _) => CheckDateText();
        }
        Closing += (_, e) => { if (DataContext is MainViewModel { Busy: true }) { e.Cancel = true; MessageBox.Show(this, "중단 요청 후 현재 요청이 끝나면 종료하세요."); } };
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
    void CheckDateText()
    {
        bool Valid(DatePicker picker)
        {
            string text = (picker.Template.FindName("PART_TextBox", picker) as DatePickerTextBox)?.Text ?? picker.Text;
            return string.IsNullOrWhiteSpace(text) ? picker.SelectedDate == null :
                DateTime.TryParse(text, out var date) && picker.SelectedDate?.Date == date.Date;
        }
        if (DataContext is MainViewModel vm) vm.DateTextEdited(Valid(StartPicker) && Valid(EndPicker));
    }
    public bool Confirm(string message) => MessageBox.Show(this, message, "메일 변경 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public NetworkCredential? Credentials()
    {
        var dialog = new LoginWindow { Owner = this };
        try { return dialog.ShowDialog() == true ? dialog.Credential : null; }
        finally { dialog.ClearSecret(); }
    }
}
