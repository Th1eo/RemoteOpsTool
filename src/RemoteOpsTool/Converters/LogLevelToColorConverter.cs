using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RemoteOpsTool.Converters;

public class LogLevelToColorConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush InfoBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5D, 0xB8, 0x72));
    private static readonly System.Windows.Media.Brush WarnBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8F, 0x73, 0xD8));
    private static readonly System.Windows.Media.Brush ErrorBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x45, 0x45));
    private static readonly System.Windows.Media.Brush DefaultBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFA, 0xF9, 0xF5));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Models.LogLevel level)
        {
            return level switch
            {
                Models.LogLevel.Info => InfoBrush,
                Models.LogLevel.Warn => WarnBrush,
                Models.LogLevel.Error => ErrorBrush,
                _ => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
