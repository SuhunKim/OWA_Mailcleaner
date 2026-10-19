using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace OwaMailCleaner.Controls
{
    public partial class OwaComboBox : UserControl
    {
        public OwaComboBox() { InitializeComponent(); }
        public string Label { get { return (string)GetValue(LabelProperty); } set { SetValue(LabelProperty, value); } }
        public IEnumerable ItemsSource { get { return (IEnumerable)GetValue(ItemsSourceProperty); } set { SetValue(ItemsSourceProperty, value); } }
        public object SelectedItem { get { return GetValue(SelectedItemProperty); } set { SetValue(SelectedItemProperty, value); } }
        public string DisplayMemberPath { get { return (string)GetValue(DisplayMemberPathProperty); } set { SetValue(DisplayMemberPathProperty, value); } }
        public string ErrorText { get { return (string)GetValue(ErrorTextProperty); } set { SetValue(ErrorTextProperty, value); } }
        public bool HasError { get { return (bool)GetValue(HasErrorProperty); } set { SetValue(HasErrorProperty, value); } }
        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register("Label", typeof(string), typeof(OwaComboBox), new PropertyMetadata(""));
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register("ItemsSource", typeof(IEnumerable), typeof(OwaComboBox));
        public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register("SelectedItem", typeof(object), typeof(OwaComboBox), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
        public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.Register("DisplayMemberPath", typeof(string), typeof(OwaComboBox), new PropertyMetadata(""));
        public static readonly DependencyProperty ErrorTextProperty = DependencyProperty.Register("ErrorText", typeof(string), typeof(OwaComboBox), new PropertyMetadata(""));
        public static readonly DependencyProperty HasErrorProperty = DependencyProperty.Register("HasError", typeof(bool), typeof(OwaComboBox), new PropertyMetadata(false));
    }
}
