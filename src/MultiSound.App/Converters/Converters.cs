using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MultiSound.App.ViewModels;

namespace MultiSound.App.Converters;

public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value is StatusKind k
            ? k switch
            {
                StatusKind.Ok => "Ok",
                StatusKind.Busy => "Accent",
                StatusKind.Warning => "Warn",
                StatusKind.Error => "Error",
                _ => "Muted",
            }
            : "Muted";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool v => v,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
