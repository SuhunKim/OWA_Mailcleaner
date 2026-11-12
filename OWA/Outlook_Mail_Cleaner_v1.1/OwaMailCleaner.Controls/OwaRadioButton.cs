using System.Windows;
using System.Windows.Controls;
namespace OwaMailCleaner.Controls { public class OwaRadioButton : RadioButton { static OwaRadioButton() { DefaultStyleKeyProperty.OverrideMetadata(typeof(OwaRadioButton), new FrameworkPropertyMetadata(typeof(OwaRadioButton))); } } }
