using System.Windows;

namespace RemoteOpsTool.Views.Dialogs;

public partial class ConfirmationDialog : Window
{
    public string DialogHeading { get; }
    public string DialogMessage { get; }
    public string ConfirmButtonText { get; }
    public string CancelButtonText { get; }

    public ConfirmationDialog(
        string title,
        string heading,
        string message,
        string confirmButtonText = "确认",
        string cancelButtonText = "取消")
    {
        Title = title;
        DialogHeading = heading;
        DialogMessage = message;
        ConfirmButtonText = confirmButtonText;
        CancelButtonText = cancelButtonText;

        InitializeComponent();

        Owner = System.Windows.Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? System.Windows.Application.Current.MainWindow;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
