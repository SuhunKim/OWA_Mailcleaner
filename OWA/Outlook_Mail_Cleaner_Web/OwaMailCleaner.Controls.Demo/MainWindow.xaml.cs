using System.Collections.ObjectModel;
using System.ComponentModel;

namespace OwaMailCleaner.Controls.Demo;
public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged
{
    private System.DateTime? _startDate = System.DateTime.Today.AddDays(-5), _endDate = System.DateTime.Today;
    public ObservableCollection<string> Folders { get; } = new() { "받은 편지함 (0)", "프로젝트 (0)", "뉴스레터 (0)" };
    public string SelectedFolder { get; set; } = "받은 편지함 (0)";
    public ObservableCollection<DemoMessage> Messages { get; } = new() { new(System.DateTime.Today, "총무팀", "9월 일보", "읽지 않음", false), new(System.DateTime.Today.AddDays(-1), "인사팀", "복지 제도 안내", "읽음", false), new(System.DateTime.Today.AddDays(-2), "시스템", "첨부파일 처리 실패", "실패", true) };
    public ObservableCollection<System.DateTime> DisabledDates { get; } = new() { System.DateTime.Today.AddDays(-1) };
    public System.DateTime MinimumDate => System.DateTime.Today.AddMonths(-3);
    public System.DateTime? StartDate { get => _startDate; set { _startDate = value; Changed(nameof(StartDate)); Changed(nameof(RangeText)); } }
    public System.DateTime? EndDate { get => _endDate; set { _endDate = value; Changed(nameof(EndDate)); Changed(nameof(RangeText)); } }
    public string RangeText => $"선택: {StartDate:yyyy-MM-dd} ~ {EndDate:yyyy-MM-dd}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public MainWindow() { InitializeComponent(); DataContext = this; }
}
public record DemoMessage(System.DateTime Received, string Sender, string Subject, string Status, bool IsErrorRow);
