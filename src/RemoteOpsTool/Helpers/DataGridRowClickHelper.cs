using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace RemoteOpsTool.Helpers;

public static class DataGridRowClickHelper
{
    private static readonly Dictionary<Type, PropertyInfo?> _isCheckedCache = [];
    private static readonly Dictionary<Type, PropertyInfo?> _rightClickedRowCache = [];
    private static readonly Dictionary<Type, PropertyInfo?> _rightClickedSessionCache = [];

    public static void HandlePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        if (grid.ContextMenu != null && grid.ContextMenu.IsOpen) return;

        var row = FindRow(e.OriginalSource);
        if (row?.DataContext is not System.ComponentModel.INotifyPropertyChanged item) return;

        grid.SelectedItem = item;

        if (e.OriginalSource is System.Windows.Controls.CheckBox) return;

        var prop = GetCachedProperty(_isCheckedCache, item.GetType(), "IsChecked");
        if (prop?.CanWrite == true)
        {
            var current = (bool)(prop.GetValue(item) ?? false);
            prop.SetValue(item, !current);
            e.Handled = true;
        }
    }

    public static void HandlePreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        var row = FindRow(e.OriginalSource);
        if (row?.DataContext == null) return;

        grid.SelectedItem = row.DataContext;

        if (grid.DataContext != null)
        {
            var ctx = grid.DataContext;
            var rowData = row.DataContext;

            var rowProp = GetCachedProperty(_rightClickedRowCache, ctx.GetType(), "RightClickedRow");
            if (rowProp != null && rowProp.PropertyType.IsAssignableFrom(rowData.GetType()))
                rowProp.SetValue(ctx, rowData);

            var sessionProp = GetCachedProperty(_rightClickedSessionCache, ctx.GetType(), "RightClickedSessionRow");
            if (sessionProp != null && sessionProp.PropertyType.IsAssignableFrom(rowData.GetType()))
                sessionProp.SetValue(ctx, rowData);
        }
    }

    private static PropertyInfo? GetCachedProperty(Dictionary<Type, PropertyInfo?> cache, Type type, string propName)
    {
        if (!cache.TryGetValue(type, out var cached))
        {
            cached = type.GetProperty(propName);
            cache[type] = cached;
        }
        return cached;
    }

    private static System.Windows.Controls.DataGridRow? FindRow(object source)
    {
        var hit = source as DependencyObject;
        while (hit != null && hit is not System.Windows.Controls.DataGridRow)
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        return hit as System.Windows.Controls.DataGridRow;
    }

    public static void HandleContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu
            || menu.PlacementTarget is not System.Windows.Controls.DataGrid grid
            || grid.DataContext == null)
            return;

        var context = grid.DataContext;
        ClearCachedProperty(context, _rightClickedRowCache, "RightClickedRow");
        ClearCachedProperty(context, _rightClickedSessionCache, "RightClickedSessionRow");
    }

    private static void ClearCachedProperty(
        object context,
        Dictionary<Type, PropertyInfo?> cache,
        string propertyName)
    {
        var property = GetCachedProperty(cache, context.GetType(), propertyName);
        if (property?.CanWrite == true)
            property.SetValue(context, null);
    }

}
