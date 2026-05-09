using System.Windows;
using System.Windows.Input;
using RemoteOpsTool.Helpers;
namespace RemoteOpsTool.Views.Dialogs;
public partial class EnvVarWindow : System.Windows.Window
{
    public EnvVarWindow() => InitializeComponent();
    private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DataGridRowClickHelper.HandlePreviewMouseDown(sender, e);
    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => DataGridRowClickHelper.HandlePreviewRightButtonDown(sender, e);
    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        => DataGridRowClickHelper.HandleContextMenuClosed(sender, e);
}
