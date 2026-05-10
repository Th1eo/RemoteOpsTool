using System.Globalization;
using System.Windows.Media;
using System.Windows.Data;

namespace RemoteOpsTool.Converters;

public class ConnectionStatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool connected)
            return connected ? System.Windows.Media.Color.FromRgb(0x5D, 0xB8, 0x72) : System.Windows.Media.Color.FromRgb(0xC6, 0x45, 0x45);
        return System.Windows.Media.Color.FromRgb(0x8E, 0x8B, 0x82);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
