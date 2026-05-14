using System.Windows;
using WpfB = System.Windows.Controls.Button;
using WpfG = System.Windows.Controls.Grid;
using WpfLB = System.Windows.Controls.ListBox;
using WpfM = System.Windows.Media;
using WpfRD = System.Windows.Controls.RowDefinition;
using WpfTxt = System.Windows.Controls.TextBlock;
using WpfTh = System.Windows.Thickness;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using WinStartLoc = System.Windows.WindowStartupLocation;
using WinResize = System.Windows.ResizeMode;
using WinVA = System.Windows.VerticalAlignment;
using WinHA = System.Windows.HorizontalAlignment;
using WinOrientation = System.Windows.Controls.Orientation;

namespace RemoteOpsTool.Views.Dialogs;

public class AccountPickerDialog : System.Windows.Window
{
    public string? SelectedAccount { get; private set; }
    private readonly WpfLB _listBox;

    public AccountPickerDialog(string host, string[] accounts)
    {
        Title = $"选择用户账户 - {host}";
        Width = 380; Height = 420;
        MinWidth = 320; MinHeight = 300;
        WindowStartupLocation = WinStartLoc.CenterOwner;
        ResizeMode = WinResize.CanResize;
        Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x10, 0x10, 0x10));
        FontFamily = System.Windows.Application.Current.TryFindResource("UiFont") as WpfM.FontFamily
            ?? new WpfM.FontFamily("JetBrains Mono, Source Han Sans SC");
        Owner = System.Windows.Application.Current.MainWindow;

        var rootGrid = new WpfG { Margin = new WpfTh(14, 12, 14, 12) };
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });

        var label = new WpfTxt
        {
            Text = $"目标主机 \"{host}\" 的本地账户：",
            Foreground = WpfM.Brushes.White,
            FontSize = 13,
            Margin = new WpfTh(0, 0, 0, 8)
        };
        WpfG.SetRow(label, 0); rootGrid.Children.Add(label);

        _listBox = new WpfLB
        {
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x16, 0x16, 0x16)),
            Foreground = WpfM.Brushes.White,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x15, 0x97, 0xFF)),
            FontSize = 14,
            FontFamily = System.Windows.Application.Current.TryFindResource("MonoFont") as WpfM.FontFamily
                ?? new WpfM.FontFamily("JetBrains Mono, Source Han Sans SC"),
            Margin = new WpfTh(0, 0, 0, 8)
        };

        foreach (var acc in accounts)
        {
            var item = new System.Windows.Controls.ListBoxItem
            {
                Content = acc,
                Padding = new WpfTh(8, 4, 8, 4)
            };
            _listBox.Items.Add(item);
        }

        _listBox.MouseDoubleClick += (_, _) => ConfirmSelection();
        _listBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) ConfirmSelection();
        };
        WpfG.SetRow(_listBox, 1); rootGrid.Children.Add(_listBox);

        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = WinOrientation.Horizontal,
            HorizontalAlignment = WinHA.Right,
            Margin = new WpfTh(0, 4, 0, 0)
        };

        var okBtn = new WpfB
        {
            Content = "确定",
            Width = 72, Height = 32,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x00, 0xA6, 0x5A)),
            Foreground = WpfM.Brushes.White,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x00, 0xA6, 0x5A)),
            Margin = new WpfTh(0, 0, 8, 0)
        };
        okBtn.Click += (_, _) => ConfirmSelection();

        var cancelBtn = new WpfB
        {
            Content = "取消",
            Width = 72, Height = 32,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x20, 0x20, 0x20)),
            Foreground = WpfM.Brushes.White,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x58, 0x6B, 0xE0)),
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => Close();

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        WpfG.SetRow(btnPanel, 2); rootGrid.Children.Add(btnPanel);

        Content = rootGrid;
        Loaded += (_, _) =>
        {
            if (_listBox.Items.Count > 0)
            {
                _listBox.SelectedIndex = 0;
                var item = (System.Windows.Controls.ListBoxItem)_listBox.SelectedItem;
                item?.Focus();
            }
        };
    }

    private void ConfirmSelection()
    {
        if (_listBox.SelectedItem is System.Windows.Controls.ListBoxItem item)
        {
            SelectedAccount = item.Content?.ToString();
            DialogResult = true;
            Close();
        }
    }
}
