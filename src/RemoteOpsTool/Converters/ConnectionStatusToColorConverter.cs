using System.Globalization;
using System.Windows.Media;
using System.Windows.Data;

namespace RemoteOpsTool.Converters;

public class ConnectionStatusToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool connected)
            return connected ? Colors.LimeGreen : Colors.Red;
        return Colors.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
