using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using RemoteOpsTool.Helpers;

namespace RemoteOpsTool.Views.Dialogs;

public partial class ServiceManagerWindow : System.Windows.Window
{
    public ServiceManagerWindow() => InitializeComponent();

    private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DataGridRowClickHelper.HandlePreviewMouseDown(sender, e);

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => DataGridRowClickHelper.HandlePreviewRightButtonDown(sender, e);

    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        => DataGridRowClickHelper.HandleContextMenuClosed(sender, e);

    private void Window_Closing(object sender, CancelEventArgs e)
        => WindowHelper.HandleWindowClosing(this, e);
}
