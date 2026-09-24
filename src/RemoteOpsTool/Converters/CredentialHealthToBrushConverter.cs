using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RemoteOpsTool.Models;

namespace RemoteOpsTool.Converters;

public sealed class CredentialHealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var resourceKey = value is CredentialHealth health
            ? health switch
            {
                CredentialHealth.Healthy => "SuccessBrush",
                CredentialHealth.NeedsReauth => "ErrorBrush",
                CredentialHealth.LocalLogonBlocked => "ErrorBrush",
                CredentialHealth.SecretUnreadable => "ErrorBrush",
                CredentialHealth.AuthorizationDenied => "WarningBrush",
                CredentialHealth.SessionConflict => "WarningBrush",
                CredentialHealth.TransportUnavailable => "OnDarkSoftBrush",
                _ => "HairlineBrush",
            }
            : "HairlineBrush";

        return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
