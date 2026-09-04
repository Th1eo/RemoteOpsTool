using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.ViewModels.Dialogs;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfDataGridCell = System.Windows.Controls.DataGridCell;
using WpfDataGridRow = System.Windows.Controls.DataGridRow;
namespace RemoteOpsTool.Views.Dialogs;
public partial class PrinterManagerWindow : System.Windows.Window
{
    public PrinterManagerWindow() => InitializeComponent();
    private async void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TryGetPrinterActionCell(e.OriginalSource, out var cell, out var row)
            && DataContext is PrinterManagerViewModel viewModel)
        {
            if (sender is WpfDataGrid grid)
                grid.SelectedItem = row;

            e.Handled = true;

            if (cell.Column == SharedPrinterColumn)
                await viewModel.SharePrinterAsync(row);
            else
                await viewModel.SetDefaultPrinterAsync(row);

            return;
        }

        DataGridRowClickHelper.HandlePreviewMouseDown(sender, e);
    }

    private void DataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => DataGridRowClickHelper.HandlePreviewRightButtonDown(sender, e);
    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        => DataGridRowClickHelper.HandleContextMenuClosed(sender, e);
    private void Window_Closing(object sender, CancelEventArgs e)
        => WindowHelper.HandleWindowClosing(this, e);

    private bool TryGetPrinterActionCell(object source, out WpfDataGridCell cell, out PrinterRow row)
    {
        cell = FindAncestor<WpfDataGridCell>(source)!;
        row = (FindAncestor<WpfDataGridRow>(source)?.DataContext as PrinterRow)!;

        return cell != null
            && row != null
            && (cell.Column == SharedPrinterColumn || cell.Column == DefaultPrinterColumn);
    }

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current != null && current is not T)
            current = VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
