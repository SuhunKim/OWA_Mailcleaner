using System.Net;
using System.Windows;
using MailCleaner.Services;

namespace MailCleaner.Views;

public partial class LoginWindow : Window
{
    public NetworkCredential? Credential { get; private set; }
    public LoginWindow() => InitializeComponent();
    public void ClearSecret() => Secret.Clear();
    void Accept(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(User.Text) || Secret.SecurePassword.Length == 0) { Notice.Text = "계정과 비밀번호를 입력하세요."; return; }
        try
        {
            if (Remember.IsChecked == true) WindowsCredentialStore.Save(User.Text.Trim(), Secret.Password);
            Credential = new NetworkCredential(User.Text.Trim(), Secret.SecurePassword);
            DialogResult = true;
        }
        catch { Notice.Text = "자격 증명을 저장하지 못했습니다. 저장 선택을 해제하거나 Windows 정책을 확인하세요."; }
    }
    void LoadSaved(object sender, RoutedEventArgs e)
    {
        try { var saved = WindowsCredentialStore.Read(); if (saved is {} value) { User.Text = value.User; Secret.Password = value.Password; Notice.Text = "WPF 앱 전용 계정을 불러왔습니다."; } else Notice.Text = "이 앱에 저장된 계정이 없습니다."; }
        catch { Notice.Text = "WPF 저장 계정에 접근하지 못했습니다."; }
    }
    void DeleteSaved(object sender, RoutedEventArgs e)
    {
        try { WindowsCredentialStore.Delete(); ClearSecret(); Notice.Text = "WPF 전용 저장 계정을 삭제했습니다."; }
        catch { Notice.Text = "WPF 저장 계정을 삭제하지 못했습니다."; }
    }
}
