namespace OwaDesktopCleaner;
using System.Windows;

public partial class EwsLoginWindow : Window
{
    public string UserName => UserNameBox.Text.Trim();
    public string Password => PasswordInput.Password;
    public EwsLoginWindow(bool manage=false, bool retry=false)
    {
        InitializeComponent();
        if(manage) { Title=Heading.Text="EWS 로그인 정보"; AcceptButton.Content="저장"; }
        try {
            var saved=WindowsCredentialStore.Read();
            if(saved is {} value) { UserNameBox.Text=value.User; PasswordInput.Password=value.Password; SaveOnPc.IsChecked=true; }
        } catch { Status.Text="저장 정보를 불러오지 못했습니다. 직접 입력하거나 저장 정보를 삭제해 주세요."; }
        if(retry)Status.Text="서버가 인증을 다시 요청했습니다. 사용자 이름과 비밀번호를 확인해 주세요.";
    }
    void Accept(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(UserName) || PasswordInput.SecurePassword.Length==0) {
            Status.Text="사용자 이름과 비밀번호를 입력하세요."; return;
        }
        try {
            if(SaveOnPc.IsChecked==true)WindowsCredentialStore.Save(UserName,Password);
            else WindowsCredentialStore.Delete();
            DialogResult=true;
        } catch { Status.Text="저장 설정을 적용하지 못했습니다. Windows 자격 증명 관리자 상태를 확인해 주세요."; }
    }
    void DeleteSaved(object sender,RoutedEventArgs e)
    {
        try {
            WindowsCredentialStore.Delete(); ClearSecret(); UserNameBox.Clear(); SaveOnPc.IsChecked=false;
            Status.Text="저장 정보를 삭제했습니다. 현재 인증 세션도 종료하려면 메인 창에서 세션 정리를 누르세요.";
        } catch { Status.Text="저장 정보를 삭제하지 못했습니다."; }
    }
    public void ClearSecret() => PasswordInput.Clear();
}
