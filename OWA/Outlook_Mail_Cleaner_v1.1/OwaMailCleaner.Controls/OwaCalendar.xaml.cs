using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OwaMailCleaner.Controls
{
    public enum OwaCalendarSelectionMode { Single, Range }

    public partial class OwaCalendar : UserControl, INotifyPropertyChanged
    {
        private DateTime _displayMonth;
        public ObservableCollection<OwaCalendarDay> Days { get; private set; }
        public string DisplayMonthText { get { return _displayMonth.ToString("yyyy년 M월"); } }
        public event PropertyChangedEventHandler PropertyChanged;
        public OwaCalendar()
        {
            InitializeComponent(); Days = new ObservableCollection<OwaCalendarDay>(); _displayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); Refresh();
        }
        public OwaCalendarSelectionMode SelectionMode { get { return (OwaCalendarSelectionMode)GetValue(SelectionModeProperty); } set { SetValue(SelectionModeProperty, value); } }
        public DateTime? SelectedDate { get { return (DateTime?)GetValue(SelectedDateProperty); } set { SetValue(SelectedDateProperty, value); } }
        public DateTime? RangeStart { get { return (DateTime?)GetValue(RangeStartProperty); } set { SetValue(RangeStartProperty, value); } }
        public DateTime? RangeEnd { get { return (DateTime?)GetValue(RangeEndProperty); } set { SetValue(RangeEndProperty, value); } }
        public DateTime? MinDate { get { return (DateTime?)GetValue(MinDateProperty); } set { SetValue(MinDateProperty, value); } }
        public DateTime? MaxDate { get { return (DateTime?)GetValue(MaxDateProperty); } set { SetValue(MaxDateProperty, value); } }
        public IList<DateTime> DisabledDates { get { return (IList<DateTime>)GetValue(DisabledDatesProperty); } set { SetValue(DisabledDatesProperty, value); } }
        public static readonly DependencyProperty SelectionModeProperty = DependencyProperty.Register("SelectionMode", typeof(OwaCalendarSelectionMode), typeof(OwaCalendar), new PropertyMetadata(OwaCalendarSelectionMode.Single, Changed));
        public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register("SelectedDate", typeof(DateTime?), typeof(OwaCalendar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
        public static readonly DependencyProperty RangeStartProperty = DependencyProperty.Register("RangeStart", typeof(DateTime?), typeof(OwaCalendar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
        public static readonly DependencyProperty RangeEndProperty = DependencyProperty.Register("RangeEnd", typeof(DateTime?), typeof(OwaCalendar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
        public static readonly DependencyProperty MinDateProperty = DependencyProperty.Register("MinDate", typeof(DateTime?), typeof(OwaCalendar), new PropertyMetadata(null, Changed));
        public static readonly DependencyProperty MaxDateProperty = DependencyProperty.Register("MaxDate", typeof(DateTime?), typeof(OwaCalendar), new PropertyMetadata(null, Changed));
        public static readonly DependencyProperty DisabledDatesProperty = DependencyProperty.Register("DisabledDates", typeof(IList<DateTime>), typeof(OwaCalendar), new PropertyMetadata(null, Changed));
        private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) { ((OwaCalendar)d).Refresh(); }
        public void Refresh()
        {
            if (Days == null) return; Days.Clear(); var first = _displayMonth; var start = first.AddDays(-(int)first.DayOfWeek);
            for (int i = 0; i < 42; i++) { var date = start.AddDays(i).Date; Days.Add(new OwaCalendarDay(date, date.Month == first.Month, IsEnabledDate(date), IsSelected(date), IsInRange(date))); }
            Raise("DisplayMonthText");
        }
        private bool IsEnabledDate(DateTime date) { return (!MinDate.HasValue || date >= MinDate.Value.Date) && (!MaxDate.HasValue || date <= MaxDate.Value.Date) && (DisabledDates == null || !DisabledDates.Any(x => x.Date == date)); }
        private bool IsSelected(DateTime date) { return SelectionMode == OwaCalendarSelectionMode.Single ? SelectedDate.HasValue && date == SelectedDate.Value.Date : (RangeStart.HasValue && date == RangeStart.Value.Date) || (RangeEnd.HasValue && date == RangeEnd.Value.Date); }
        private bool IsInRange(DateTime date) { return SelectionMode == OwaCalendarSelectionMode.Range && RangeStart.HasValue && RangeEnd.HasValue && date > RangeStart.Value.Date && date < RangeEnd.Value.Date; }
        private void SelectDay(object sender, RoutedEventArgs e)
        {
            var day = ((Button)sender).Tag as OwaCalendarDay; if (day == null || !day.IsEnabled) return; Select(day.Date);
        }
        public void Select(DateTime date)
        {
            if (!IsEnabledDate(date.Date)) return;
            if (SelectionMode == OwaCalendarSelectionMode.Single) SelectedDate = date.Date;
            else if (!RangeStart.HasValue || RangeEnd.HasValue) { RangeStart = date.Date; RangeEnd = null; }
            else if (date.Date < RangeStart.Value.Date) { RangeEnd = RangeStart; RangeStart = date.Date; } else RangeEnd = date.Date;
            Refresh();
        }
        private void PreviousMonth(object sender, RoutedEventArgs e) { _displayMonth = _displayMonth.AddMonths(-1); Refresh(); }
        private void NextMonth(object sender, RoutedEventArgs e) { _displayMonth = _displayMonth.AddMonths(1); Refresh(); }
        private void CalendarKeyDown(object sender, KeyEventArgs e)
        {
            DateTime focus = SelectedDate ?? RangeEnd ?? RangeStart ?? DateTime.Today;
            if (e.Key == Key.Left) focus = focus.AddDays(-1); else if (e.Key == Key.Right) focus = focus.AddDays(1); else if (e.Key == Key.Up) focus = focus.AddDays(-7); else if (e.Key == Key.Down) focus = focus.AddDays(7); else return;
            _displayMonth = new DateTime(focus.Year, focus.Month, 1); Select(focus); e.Handled = true;
        }
        private void Raise(string name) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name)); }
    }
    public class OwaCalendarDay
    {
        public OwaCalendarDay(DateTime date, bool current, bool enabled, bool selected, bool inRange) { Date = date; DayText = current ? date.Day.ToString() : ""; IsEnabled = enabled; Background = selected ? new SolidColorBrush(Color.FromRgb(15,108,189)) : inRange ? new SolidColorBrush(Color.FromRgb(231,243,255)) : Brushes.White; Foreground = selected ? Brushes.White : current ? new SolidColorBrush(Color.FromRgb(23,32,51)) : Brushes.Transparent; }
        public DateTime Date { get; private set; } public string DayText { get; private set; } public bool IsEnabled { get; private set; } public Brush Background { get; private set; } public Brush Foreground { get; private set; }
    }
}
