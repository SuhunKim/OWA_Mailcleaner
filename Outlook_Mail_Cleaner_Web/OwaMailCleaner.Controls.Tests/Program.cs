using OwaMailCleaner.Controls;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var calendar = new OwaCalendar { SelectionMode = OwaCalendarSelectionMode.Range, MinDate = DateTime.Today.AddDays(-10), MaxDate = DateTime.Today.AddDays(10), DisabledDates = new List<DateTime> { DateTime.Today.AddDays(3) } };
            calendar.Select(DateTime.Today); calendar.Select(DateTime.Today.AddDays(-2));
            Assert(calendar.RangeStart == DateTime.Today.AddDays(-2) && calendar.RangeEnd == DateTime.Today, "역순 기간 선택을 정렬해야 합니다.");
            calendar.Select(DateTime.Today.AddDays(3)); Assert(calendar.RangeStart == DateTime.Today.AddDays(-2), "비활성 날짜는 선택되면 안 됩니다.");
            calendar.SelectionMode = OwaCalendarSelectionMode.Single; calendar.Select(DateTime.Today.AddDays(1)); Assert(calendar.SelectedDate == DateTime.Today.AddDays(1), "단일 날짜 선택이 실패했습니다.");
            var button = new OwaButton { Variant = OwaButtonVariant.Danger }; Assert(button.Variant == OwaButtonVariant.Danger, "버튼 Variant DP가 실패했습니다.");
            var combo = new OwaComboBox { Label = "폴더", HasError = true }; Assert(combo.Label == "폴더" && combo.HasError, "콤보박스 상태 DP가 실패했습니다.");
            var grid = new OwaDataGrid(); OwaDataGrid.SetIsErrorRow(grid, true); Assert(OwaDataGrid.GetIsErrorRow(grid), "그리드 오류 행 속성이 실패했습니다.");
            var separator = new OwaSeparator { Orientation = System.Windows.Controls.Orientation.Vertical }; Assert(separator.Orientation == System.Windows.Controls.Orientation.Vertical, "구분선 방향이 실패했습니다.");
            Console.WriteLine("All OwaMailCleaner.Controls checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
