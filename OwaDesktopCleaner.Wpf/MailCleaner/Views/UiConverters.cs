using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MailCleaner.Views;

public sealed class ModeSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}
public sealed class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? "";
        bool fg = parameter as string == "Foreground";
        string color = text.StartsWith("성공 ", StringComparison.Ordinal) ? (fg ? "#93370D" : "#FFFAEB") :
            text.Contains("실패") || text.Contains("거부") || text.Contains("만료") || text.Contains("오류") ? (fg ? "#B42318" : "#FEF3F2") :
            text.Contains("중단") || text.Contains("다시 검색") || text.Contains("미확인") ? (fg ? "#93370D" : "#FFFAEB") :
            text.Contains("완료") || text.Contains("연결 성공") ? (fg ? "#067647" : "#ECFDF3") : (fg ? "#24547F" : "#EEF5FC");
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class FilterHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        // Let filters scroll on short windows while keeping space for the mail list.
        // The body itself stretches with the native window; no fixed modal dimensions.
        if (values[0] is not double height || height <= 0) return 0d;
        double toolbar = values.Length > 1 && values[1] is double n ? n : 0;
        if (values.Length > 2 && values[2] is Thickness margin) toolbar += margin.Top + margin.Bottom;
        return Math.Max(0, (height - toolbar) * 0.7);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
