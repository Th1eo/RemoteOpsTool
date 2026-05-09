using System.ComponentModel;
using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Views.Dialogs;

public partial class SystemInfoWindow : System.Windows.Window
{
    public SystemInfoWindow() => InitializeComponent();

    private void Window_Closing(object sender, CancelEventArgs e)
        => WindowHelper.HandleWindowClosing(this, e);
}
