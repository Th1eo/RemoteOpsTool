using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.ViewModels.Dialogs;
using WpfCM = System.Windows.Controls.ContextMenu;
using WpfMI = System.Windows.Controls.MenuItem;
using WpfSep = System.Windows.Controls.Separator;

namespace RemoteOpsTool.Views.Dialogs;

public partial class RemoteRegistryWindow : Window
{
    private readonly RemoteRegistryViewModel _vm;
    private Style? _cmStyle, _miStyle;
    private TreeViewItem? _lastRightClickedTreeItem;

    public RemoteRegistryWindow(RemoteRegistryViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.LoadHives();
        CacheStyles();
        BuildMenus();
    }

    private void CacheStyles()
    {
        _cmStyle = TryFindResource(typeof(WpfCM)) as Style;
        _miStyle = TryFindResource(typeof(WpfMI)) as Style;
    }

    private void BuildMenus()
    {
        RegistryTree.ContextMenu = MakeTreeMenu();
        ValueListBox.ContextMenu = MakeValueMenu();
    }

    // ═══ Styling helpers ═══

    private WpfCM MakeBaseMenu()
    {
        var menu = new WpfCM();
        if (_cmStyle != null) menu.Style = _cmStyle;
        return menu;
    }

    private WpfMI MakeMenuItem(string header)
    {
        var item = new WpfMI { Header = header };
        if (_miStyle != null) item.Style = _miStyle;
        return item;
    }

    private static WpfSep MakeDarkSeparator()
    {
        var sep = new WpfSep();
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)));
        f.SetValue(Border.HeightProperty, 1.0);
        f.SetValue(Border.MarginProperty, new Thickness(8, 4, 8, 4));
        sep.Template = new ControlTemplate(typeof(WpfSep)) { VisualTree = f };
        return sep;
    }

    private static void CloseMenuChain(WpfMI item)
    {
        var current = item.Parent;
        while (current != null)
        {
            if (current is WpfCM cm) { cm.IsOpen = false; return; }
            current = current is FrameworkElement fe ? fe.Parent : null;
        }
    }

    // ═══ Tree key menu ═══

    private WpfCM MakeTreeMenu()
    {
        var menu = MakeBaseMenu();

        var expand = MakeMenuItem("展开(_E)");
        expand.Click += (_, _) =>
        {
            var item = _lastRightClickedTreeItem;
            if (item != null)
            {
                item.IsExpanded = true;
                _lastRightClickedTreeItem = null;
            }
        };
        menu.Items.Add(expand);

        var newItem = MakeMenuItem("新建(_N)");
        newItem.StaysOpenOnClick = true;
        FillNewSubItems(newItem, isTreeMenu: true);
        menu.Items.Add(newItem);

        var delete = MakeMenuItem("删除(_D)");
        delete.Click += (_, _) => _vm.DeleteKeyCommand.Execute(null);
        menu.Items.Add(delete);

        var rename = MakeMenuItem("重命名(_R)");
        rename.Click += (_, _) => _vm.RenameKeyCommand.Execute(null);
        menu.Items.Add(rename);

        return menu;
    }

    // ═══ Value area menu ═══

    private WpfCM MakeValueMenu()
    {
        var menu = MakeBaseMenu();

        var modify = MakeMenuItem("修改(_M)...");
        modify.Click += (_, _) => _vm.ModifyValueCommand.Execute(null);
        menu.Items.Add(modify);
        menu.Items.Add(MakeDarkSeparator());

        var rename = MakeMenuItem("重命名(_R)");
        rename.Click += (_, _) => _vm.RenameValueCommand.Execute(null);
        menu.Items.Add(rename);

        var delete = MakeMenuItem("删除(_D)");
        delete.Click += (_, _) => _vm.DeleteValueCommand.Execute(null);
        menu.Items.Add(delete);

        menu.Items.Add(MakeDarkSeparator());

        var newItem = MakeMenuItem("新建(_N)");
        newItem.StaysOpenOnClick = true;
        FillNewSubItems(newItem, isTreeMenu: false);
        menu.Items.Add(newItem);

        return menu;
    }

    // ═══ Shared sub-items ═══

    private void FillNewSubItems(WpfMI parent, bool isTreeMenu)
    {
        // "项" only for tree menu; for value area menu, also include "项"
        var subKey = MakeMenuItem("项(_K)");
        subKey.Click += (_, _) => { _vm.NewKeyCommand.Execute(null); CloseMenuChain(parent); };
        parent.Items.Add(subKey);
        parent.Items.Add(MakeDarkSeparator());

        AddTypeItem(parent, "字符串值(_S)", () => _vm.NewStringValueCommand.Execute(null));
        AddTypeItem(parent, "二进制值(_B)", () => _vm.NewBinaryValueCommand.Execute(null));
        AddTypeItem(parent, "DWORD (32 位)值(_D)", () => _vm.NewDwordValueCommand.Execute(null));
        AddTypeItem(parent, "QWORD (64 位)值(_Q)", () => _vm.NewQwordValueCommand.Execute(null));
        AddTypeItem(parent, "多字符串值(_M)", () => _vm.NewMultiStringValueCommand.Execute(null));
        AddTypeItem(parent, "可扩充字符串值(_E)", () => _vm.NewExpandStringValueCommand.Execute(null));
    }

    private void AddTypeItem(WpfMI parent, string header, Action action)
    {
        var item = MakeMenuItem(header);
        item.Click += (_, _) => { action(); CloseMenuChain(parent); };
        parent.Items.Add(item);
    }

    // ═══ TreeView ═══

    private void RegistryTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && item.DataContext is RegistryTreeNode node)
        {
            e.Handled = true;
            _vm.LoadChildrenAsync(node);
        }
    }

    private void RegistryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is RegistryTreeNode node && !string.IsNullOrEmpty(node.FullPath))
        {
            _vm.CurrentPath = node.FullPath;
            _vm.NavigateCommand.Execute(null);
        }
    }

    private void RegistryTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var tv = (System.Windows.Controls.TreeView)sender;
        var hit = VisualTreeHelper.HitTest(tv, e.GetPosition(tv));
        var item = hit?.VisualHit != null ? FindAncestor<TreeViewItem>(hit.VisualHit) : null;
        _lastRightClickedTreeItem = item;
        if (item != null)
            item.IsSelected = true;
    }

    // ═══ ListBox ═══

    private void ValueListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var lb = (System.Windows.Controls.ListBox)sender;
        if (HitTestItem(lb, e) is RegValueDisplay)
            _vm.ModifyValueCommand.Execute(null);
    }

    private void ValueListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lb = (System.Windows.Controls.ListBox)sender;
        var val = HitTestItem(lb, e);
        lb.SelectedItem = val;

        var menu = (WpfCM)lb.ContextMenu;
        bool hasVal = val != null;

        // Items: [0]=modify, [1]=sep, [2]=rename, [3]=delete, [4]=sep, [5]=new
        ((WpfMI)menu.Items[0]).Visibility = hasVal ? Visibility.Visible : Visibility.Collapsed;
        ((WpfSep)menu.Items[1]).Visibility = hasVal ? Visibility.Visible : Visibility.Collapsed;
        ((WpfMI)menu.Items[2]).Visibility = hasVal ? Visibility.Visible : Visibility.Collapsed;
        ((WpfMI)menu.Items[3]).Visibility = hasVal ? Visibility.Visible : Visibility.Collapsed;
        ((WpfSep)menu.Items[4]).Visibility = hasVal ? Visibility.Visible : Visibility.Collapsed;
        ((WpfMI)menu.Items[5]).Visibility = hasVal ? Visibility.Collapsed : Visibility.Visible;

        if (hasVal)
            ((WpfMI)menu.Items[2]).IsEnabled = !_vm.IsDefaultValueSelected;
    }

    private static RegValueDisplay? HitTestItem(System.Windows.Controls.ListBox lb, MouseButtonEventArgs e)
    {
        var hit = VisualTreeHelper.HitTest(lb, e.GetPosition(lb));
        if (hit?.VisualHit == null) return null;
        var item = FindAncestor<ListBoxItem>(hit.VisualHit);
        return item?.DataContext as RegValueDisplay;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ═══ Path bar ═══

    private void PathBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _vm.NavigateCommand.Execute(null);
    }

    // ═══ Window close ═══

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        => WindowHelper.HandleWindowClosing(this, e);

    // ═══ Bottom button ═══

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    // ═══ Button menu handlers ═══

    private void NewSubKey_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewKeyCommand.Execute(null);
    private void NewStringValue_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewStringValueCommand.Execute(null);
    private void NewBinaryValue_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewBinaryValueCommand.Execute(null);
    private void NewDwordValue_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewDwordValueCommand.Execute(null);
    private void NewQwordValue_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewQwordValueCommand.Execute(null);
    private void NewMultiStringValue_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewMultiStringValueCommand.Execute(null);
    private void NewExpandStringValue_MenuClick(object sender, RoutedEventArgs e)
        => _vm.NewExpandStringValueCommand.Execute(null);
}
