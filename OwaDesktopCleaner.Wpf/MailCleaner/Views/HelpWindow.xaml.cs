using System.Windows;
using System.Windows.Input;

namespace MailCleaner.Views;

public partial class HelpWindow : Window
{
    // Static XAML only: no account, settings, transport or mailbox access.
    public HelpWindow() => InitializeComponent();
    void CloseWindow(object sender, RoutedEventArgs e) => Close();
    void CloseOnEscape(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
