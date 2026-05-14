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

public class NewKeyDialog : System.Windows.Window
{
    public string? KeyName { get; private set; }

    public NewKeyDialog()
    {
        Title = "新建项";
        Width = 400; Height = 190;
        MinWidth = 340;
        WindowStartupLocation = WinStartLoc.CenterOwner;
        ResizeMode = WinResize.NoResize;
        Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x10, 0x10, 0x10));
        FontFamily = System.Windows.Application.Current.TryFindResource("UiFont") as WpfM.FontFamily
            ?? new WpfM.FontFamily("JetBrains Mono, Source Han Sans SC");
        Owner = System.Windows.Application.Current.MainWindow;

        var rootGrid = new WpfG { Margin = new WpfTh(14, 12, 14, 12) };
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });

        var label = new WpfTxt
        {
            Text = "项名称(_K):",
            Foreground = WpfM.Brushes.LightGray,
            FontSize = 13,
            Margin = new WpfTh(0, 0, 0, 3)
        };
        WpfG.SetRow(label, 0); rootGrid.Children.Add(label);

        var nameBox = new WpfTB
        {
            Height = 28,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x16, 0x16, 0x16)),
            Foreground = WpfM.Brushes.LightGray,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x4B, 0x55, 0x63)),
            FontSize = 14,
            VerticalContentAlignment = WinVA.Center,
            Padding = new WpfTh(6, 0, 6, 0),
            Margin = new WpfTh(0, 22, 0, 8)
        };
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && !string.IsNullOrWhiteSpace(nameBox.Text))
            {
                KeyName = nameBox.Text.Trim();
                DialogResult = true;
                Close();
            }
        };
        WpfG.SetRow(nameBox, 0); rootGrid.Children.Add(nameBox);

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
            Margin = new WpfTh(0, 0, 8, 0),
            IsDefault = true
        };
        okBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text))
            {
                KeyName = nameBox.Text.Trim();
                DialogResult = true;
                Close();
            }
        };

        var cancelBtn = new WpfB
        {
            Content = "取消",
            Width = 72, Height = 32,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x20, 0x20, 0x20)),
            Foreground = WpfM.Brushes.LightGray,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x4B, 0x55, 0x63)),
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => Close();

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        WpfG.SetRow(btnPanel, 1); rootGrid.Children.Add(btnPanel);

        Content = rootGrid;
        Loaded += (_, _) => nameBox.Focus();
    }
}
