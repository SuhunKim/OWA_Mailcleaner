using System.Windows;
using System.Windows.Controls;
namespace OwaMailCleaner.Controls
{
    public class OwaDataGrid : DataGrid
    {
        static OwaDataGrid() { DefaultStyleKeyProperty.OverrideMetadata(typeof(OwaDataGrid), new FrameworkPropertyMetadata(typeof(OwaDataGrid))); }
        public static bool GetIsErrorRow(DependencyObject element) { return (bool)element.GetValue(IsErrorRowProperty); }
        public static void SetIsErrorRow(DependencyObject element, bool value) { element.SetValue(IsErrorRowProperty, value); }
        public static readonly DependencyProperty IsErrorRowProperty = DependencyProperty.RegisterAttached("IsErrorRow", typeof(bool), typeof(OwaDataGrid), new PropertyMetadata(false));
    }
}
