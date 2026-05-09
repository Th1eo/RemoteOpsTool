using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RemoteOpsTool.Converters;

public class LogLevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Models.LogLevel level)
        {
            return level switch
            {
                Models.LogLevel.Info => System.Windows.Media.Brushes.LightGreen,
                Models.LogLevel.Warn => System.Windows.Media.Brushes.Orange,
                Models.LogLevel.Error => System.Windows.Media.Brushes.OrangeRed,
                _ => System.Windows.Media.Brushes.White
            };
        }
        return System.Windows.Media.Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
