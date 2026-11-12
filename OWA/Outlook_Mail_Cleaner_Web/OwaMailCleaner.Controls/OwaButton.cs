using System.Windows;
using System.Windows.Controls;

namespace OwaMailCleaner.Controls
{
    public enum OwaButtonVariant { Primary, Secondary, Danger }

    public class OwaButton : Button
    {
        static OwaButton() { DefaultStyleKeyProperty.OverrideMetadata(typeof(OwaButton), new FrameworkPropertyMetadata(typeof(OwaButton))); }
        public OwaButtonVariant Variant { get { return (OwaButtonVariant)GetValue(VariantProperty); } set { SetValue(VariantProperty, value); } }
        public static readonly DependencyProperty VariantProperty = DependencyProperty.Register("Variant", typeof(OwaButtonVariant), typeof(OwaButton), new PropertyMetadata(OwaButtonVariant.Primary));
    }
}
