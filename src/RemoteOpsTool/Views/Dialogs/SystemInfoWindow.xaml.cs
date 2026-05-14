using System.ComponentModel;
using System.Windows;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.ViewModels.Dialogs;

namespace RemoteOpsTool.Views.Dialogs;

public partial class SystemInfoWindow : System.Windows.Window
{
    private SystemInfoViewModel? _vm;

    public SystemInfoWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = e.NewValue as SystemInfoViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            RefreshDocument();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemInfoViewModel.InfoDocument))
            RefreshDocument();
    }

    private void RefreshDocument()
    {
        if (_vm?.InfoDocument != null)
        {
            InfoRichTextBox.Document = _vm.InfoDocument;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
        => WindowHelper.HandleWindowClosing(this, e);

    private void InfoTextBox_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(InfoRichTextBox.Selection.Text))
            System.Windows.Clipboard.SetText(InfoRichTextBox.Selection.Text);
    }

    private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        InfoRichTextBox.Focus();
        InfoRichTextBox.SelectAll();
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e)
        => DataGridRowClickHelper.HandleContextMenuClosed(sender, e);
}
