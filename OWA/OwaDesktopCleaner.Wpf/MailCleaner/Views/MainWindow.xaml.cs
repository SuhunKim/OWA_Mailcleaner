using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using MailCleaner.Models;
using MailCleaner.ViewModels;

namespace MailCleaner.Views;

public partial class MainWindow : Window, IUserDialogs
{
    HelpWindow? helpWindow;
    void ShowHelp(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (helpWindow != null)
        {
            if (helpWindow.WindowState == WindowState.Minimized) helpWindow.WindowState = WindowState.Normal;
            helpWindow.Activate(); return;
        }
        helpWindow = new HelpWindow { Owner = this };
        helpWindow.Closed += (_, _) => helpWindow = null;
        helpWindow.Show();
    }
    public MainWindow() : this(null) { }
    public MainWindow(Func<IUserDialogs, MainViewModel>? viewModelFactory)
    {
        InitializeComponent();
        if (DesignerProperties.GetIsInDesignMode(this)) return;
        DataContext = viewModelFactory?.Invoke(this) ?? new MainViewModel(this);
        ((MainViewModel)DataContext).PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.SelectedSort)) ApplySort(); };
        Loaded += (_, _) => ApplySort();
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
            string text = (picker.Template?.FindName("PART_TextBox", picker) as DatePickerTextBox)?.Text ?? picker.Text;
            return string.IsNullOrWhiteSpace(text) ? picker.SelectedDate == null :
                DateTime.TryParse(text, out var date) && picker.SelectedDate?.Date == date.Date;
        }
        if (DataContext is MainViewModel vm) vm.DateTextEdited(Valid(StartPicker) && Valid(EndPicker));
    }
    void CloseWindow(object sender, RoutedEventArgs e) => Close();
    void CopyDiagnostic(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { HasDiagnostic: true } vm) return;
        try { Clipboard.SetText(vm.DiagnosticText); }
        catch { MessageBox.Show(this, "클립보드를 사용할 수 없습니다. 진단 텍스트를 선택해 복사하세요."); }
    }
    void ApplySort()
    {
        if (DataContext is not MainViewModel vm) return;
        if (CollectionViewSource.GetDefaultView(vm.Mails) is ListCollectionView view) view.CustomSort = new MailItemComparer(vm.SelectedSort.Value);
        foreach (var column in MailGrid.Columns)
        {
            bool size = vm.SelectedSort.Value is MailSort.SizeAsc or MailSort.SizeDesc;
            column.SortDirection = column.SortMemberPath == (size ? "Size" : "Received") ?
                vm.SelectedSort.Value is MailSort.SizeAsc or MailSort.DateAsc ? ListSortDirection.Ascending : ListSortDirection.Descending : null;
        }
    }
    void SortMailGrid(object sender, DataGridSortingEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.Column.SortMemberPath is not ("Received" or "Size")) return;
        e.Handled = true;
        bool size = e.Column.SortMemberPath == "Size";
        var target = size ? vm.SelectedSort.Value == MailSort.SizeDesc ? MailSort.SizeAsc : MailSort.SizeDesc : vm.SelectedSort.Value == MailSort.DateDesc ? MailSort.DateAsc : MailSort.DateDesc;
        vm.SelectedSort = vm.SortOptions.Single(x => x.Value == target);
    }
    public bool Confirm(string message) => MessageBox.Show(this, message, "메일 변경 최종 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public NetworkCredential? Credentials()
    {
        var dialog = new LoginWindow { Owner = this };
        try { return dialog.ShowDialog() == true ? dialog.Credential : null; }
        finally { dialog.ClearSecret(); }
    }
}
