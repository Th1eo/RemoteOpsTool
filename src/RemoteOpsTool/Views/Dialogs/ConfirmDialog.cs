using System.Windows;
using WpfB = System.Windows.Controls.Button;
using WpfG = System.Windows.Controls.Grid;
using WpfM = System.Windows.Media;
using WpfTB = System.Windows.Controls.TextBox;
using WpfTxt = System.Windows.Controls.TextBlock;
using WpfRD = System.Windows.Controls.RowDefinition;
using WpfTh = System.Windows.Thickness;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using WinStartLoc = System.Windows.WindowStartupLocation;
using WinResize = System.Windows.ResizeMode;
using WinVA = System.Windows.VerticalAlignment;
using WinHA = System.Windows.HorizontalAlignment;
using WinOrientation = System.Windows.Controls.Orientation;

namespace RemoteOpsTool.Views.Dialogs;

public class ConfirmDialog : System.Windows.Window
{
    public bool Confirmed { get; private set; }
    private WpfTB _inputBox = null!;

    public ConfirmDialog(string title, string message, string confirmText = "yes")
    {
        Title = title;
        Width = 420; Height = 180;
        MinWidth = 360;
        WindowStartupLocation = WinStartLoc.CenterOwner;
        ResizeMode = WinResize.NoResize;
        Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x1E, 0x1E, 0x1E));
        Owner = System.Windows.Application.Current.MainWindow;

        var rootGrid = new WpfG { Margin = new WpfTh(14, 12, 14, 12) };
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });

        var msgBlock = new WpfTxt
        {
            Text = message,
            Foreground = WpfM.Brushes.LightGray,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new WpfTh(0, 0, 0, 8)
        };
        WpfG.SetRow(msgBlock, 0); rootGrid.Children.Add(msgBlock);

        var promptBlock = new WpfTxt
        {
            Text = $"输入 \"{confirmText}\" 确认删除：",
            Foreground = WpfM.Brushes.LightGray,
            FontSize = 12,
            Margin = new WpfTh(0, 0, 0, 4)
        };
        WpfG.SetRow(promptBlock, 1); rootGrid.Children.Add(promptBlock);

        _inputBox = new WpfTB
        {
            Height = 28,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x2D, 0x2D, 0x2D)),
            Foreground = WpfM.Brushes.White,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x55, 0x55, 0x55)),
            FontSize = 14,
            VerticalContentAlignment = WinVA.Center,
            Padding = new WpfTh(6, 0, 6, 0),
            Margin = new WpfTh(0, 22, 0, 8)
        };
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && _inputBox.Text.Trim().Equals(confirmText, StringComparison.OrdinalIgnoreCase))
            {
                Confirmed = true;
                DialogResult = true;
                Close();
            }
        };
        WpfG.SetRow(_inputBox, 1); rootGrid.Children.Add(_inputBox);

        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = WinOrientation.Horizontal,
            HorizontalAlignment = WinHA.Right,
            Margin = new WpfTh(0, 4, 0, 0)
        };

        var okBtn = new WpfB
        {
            Content = "确定",
            Width = 72, Height = 28,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x8B, 0x1E, 0x1E)),
            Foreground = WpfM.Brushes.White,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0xC5, 0x30, 0x30)),
            Margin = new WpfTh(0, 0, 8, 0)
        };
        okBtn.Click += (_, _) =>
        {
            if (_inputBox.Text.Trim().Equals(confirmText, StringComparison.OrdinalIgnoreCase))
            {
                Confirmed = true;
                DialogResult = true;
                Close();
            }
        };

        var cancelBtn = new WpfB
        {
            Content = "取消",
            Width = 72, Height = 28,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = WpfM.Brushes.LightGray,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x55, 0x55, 0x55)),
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => Close();

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        WpfG.SetRow(btnPanel, 2); rootGrid.Children.Add(btnPanel);

        Content = rootGrid;
        Loaded += (_, _) => _inputBox.Focus();
    }
}
