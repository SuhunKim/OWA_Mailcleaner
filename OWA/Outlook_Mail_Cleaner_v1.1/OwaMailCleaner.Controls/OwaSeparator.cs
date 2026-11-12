using System.Windows;
using System.Windows.Controls;
namespace OwaMailCleaner.Controls
{
    public class OwaSeparator : Separator
    {
        static OwaSeparator() { DefaultStyleKeyProperty.OverrideMetadata(typeof(OwaSeparator), new FrameworkPropertyMetadata(typeof(OwaSeparator))); }
        public Orientation Orientation { get { return (Orientation)GetValue(OrientationProperty); } set { SetValue(OrientationProperty, value); } }
        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register("Orientation", typeof(Orientation), typeof(OwaSeparator), new PropertyMetadata(Orientation.Horizontal));
    }
}
