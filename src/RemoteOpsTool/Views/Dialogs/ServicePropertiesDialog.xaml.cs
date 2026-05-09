using System.Windows;

namespace RemoteOpsTool.Views.Dialogs;

public partial class ServicePropertiesDialog : Window
{
    public ServicePropertiesDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
