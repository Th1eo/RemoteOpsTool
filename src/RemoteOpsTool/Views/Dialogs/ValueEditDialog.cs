using WpfApp = System.Windows.Application;
using WpfB = System.Windows.Controls.Button;
using WpfG = System.Windows.Controls.Grid;
using WpfM = System.Windows.Media;
using WpfRB = System.Windows.Controls.RadioButton;
using WpfSP = System.Windows.Controls.StackPanel;
using WpfTB = System.Windows.Controls.TextBox;
using WpfTxt = System.Windows.Controls.TextBlock;
using WpfRD = System.Windows.Controls.RowDefinition;
using WpfTh = System.Windows.Thickness;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using WinStartLoc = System.Windows.WindowStartupLocation;
using WinResize = System.Windows.ResizeMode;
using WinFontW = System.Windows.FontWeights;
using WinVA = System.Windows.VerticalAlignment;
using WinVis = System.Windows.Visibility;
using WinHA = System.Windows.HorizontalAlignment;
using WinOrientation = System.Windows.Controls.Orientation;

namespace RemoteOpsTool.Views.Dialogs;

public class ValueEditDialog : System.Windows.Window
{
    public string? ValueName { get; private set; }
    public string? ValueData { get; private set; }

    private readonly string _type;
    private readonly string _currentValue;
    private WpfTB _nameBox = null!;
    private WpfTB _dataBox = null!;
    private WpfRB _hexRadio = null!;
    private WpfRB _decRadio = null!;
    private WpfTxt _statusLabel = null!;
    private bool _suppressBaseChange;

    public ValueEditDialog(string name, string type, string currentValue)
    {
        _type = type;
        _currentValue = currentValue;
        Title = "编辑值";
        InitUI(name);
    }

    public ValueEditDialog(string type) : this("", type, type switch
    {
        "REG_DWORD" or "REG_DWORD_LITTLE_ENDIAN" or "REG_DWORD_BIG_ENDIAN" => "0",
        "REG_QWORD" or "REG_QWORD_LITTLE_ENDIAN" => "0",
        _ => ""
    })
    {
        Title = $"新建值 - {type}";
    }

    private void InitUI(string name)
    {
        Width = 460;
        Height = IsNumericTypeFor(_type) ? 320 : 350;
        MinHeight = IsNumericTypeFor(_type) ? 280 : 300;
        MinWidth = 400;
        WindowStartupLocation = WinStartLoc.CenterOwner;
        ResizeMode = WinResize.CanResize;
        Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x10, 0x10, 0x10));
        FontFamily = WpfApp.Current.TryFindResource("UiFont") as WpfM.FontFamily
            ?? new WpfM.FontFamily("JetBrains Mono, Source Han Sans SC");
        Owner = WpfApp.Current.MainWindow;

        var rootGrid = new WpfG { Margin = new WpfTh(14, 12, 14, 12) };
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });
        rootGrid.RowDefinitions.Add(new WpfRD { Height = new GridLength(1, GridUnitType.Auto) });

        var row = 0;
        BuildNameRow(rootGrid, row++, name);
        BuildTypeRow(rootGrid, row++, _type);
        BuildDataRow(rootGrid, row++, _currentValue);
        if (IsNumericTypeFor(_type))
            BuildBaseRow(rootGrid, row++);
        BuildButtonsRow(rootGrid, row);

        Content = rootGrid;
        Loaded += (_, _) => _nameBox.Focus();
    }

    private void BuildNameRow(WpfG g, int r, string name)
    {
        var lbl = new WpfTxt
        {
            Text = "数值名称(_N):",
            Foreground = WpfM.Brushes.LightGray,
            Margin = new WpfTh(0, 0, 0, 3),
            FontSize = 13
        };
        WpfG.SetRow(lbl, r); g.Children.Add(lbl);

        _nameBox = new WpfTB
        {
            Text = name,
            Height = 24,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x16, 0x16, 0x16)),
            Foreground = WpfM.Brushes.LightGray,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x4B, 0x55, 0x63)),
            FontSize = 13, Margin = new WpfTh(0, 22, 0, 6),
            Padding = new WpfTh(4, 0, 4, 0)
        };
        WpfG.SetRow(_nameBox, r); g.Children.Add(_nameBox);
    }

    private void BuildTypeRow(WpfG g, int r, string type)
    {
        var lbl = new WpfTxt
        {
            Text = "数值类型:",
            Foreground = WpfM.Brushes.LightGray,
            FontSize = 13, Margin = new WpfTh(0, 4, 0, 3)
        };
        WpfG.SetRow(lbl, r); g.Children.Add(lbl);

        var typeLbl = new WpfTxt
        {
            Text = type,
            Foreground = WpfM.Brushes.White,
            FontSize = 13, FontWeight = WinFontW.Bold,
            Margin = new WpfTh(0, 22, 0, 6)
        };
        WpfG.SetRow(typeLbl, r); g.Children.Add(typeLbl);
    }

    private void BuildDataRow(WpfG g, int r, string val)
    {
        var lbl = new WpfTxt
        {
            Text = "数值数据(_V):",
            Foreground = WpfM.Brushes.LightGray,
            Margin = new WpfTh(0, 4, 0, 3),
            FontSize = 13
        };
        WpfG.SetRow(lbl, r); g.Children.Add(lbl);

        var border = new System.Windows.Controls.Border
        {
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x16, 0x16, 0x16)),
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x4B, 0x55, 0x63)),
            BorderThickness = new WpfTh(1),
            Margin = new WpfTh(0, 22, 0, 6),
            SnapsToDevicePixels = true
        };

        _dataBox = new WpfTB
        {
            Text = val,
            Background = WpfM.Brushes.Transparent,
            Foreground = WpfM.Brushes.White,
            BorderThickness = new WpfTh(0),
            FontSize = 13,
            FontFamily = System.Windows.Application.Current.TryFindResource("MonoFont") as WpfM.FontFamily
                ?? new WpfM.FontFamily("JetBrains Mono, Source Han Sans SC"),
            Padding = new WpfTh(4, 2, 4, 2),
            AcceptsReturn = true,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            VerticalContentAlignment = WinVA.Top,
            MinHeight = 80
        };

        border.Child = _dataBox;
        WpfG.SetRow(border, r); g.Children.Add(border);
    }

    private void BuildBaseRow(WpfG g, int r)
    {
        var pnl = new WpfSP
        {
            Orientation = WinOrientation.Horizontal,
            HorizontalAlignment = WinHA.Left,
            Margin = new WpfTh(0, 4, 0, 8)
        };

        _hexRadio = new WpfRB
        {
            Content = "十六进制(_H)",
            Foreground = WpfM.Brushes.LightGray,
            FontSize = 13, Margin = new WpfTh(0, 0, 24, 0)
        };
        _decRadio = new WpfRB
        {
            Content = "十进制(_D)",
            Foreground = WpfM.Brushes.LightGray,
            FontSize = 13
        };

        _hexRadio.Checked += (_, _) => { if (!_suppressBaseChange) TryConvertBase(false, true); };
        _decRadio.Checked += (_, _) => { if (!_suppressBaseChange) TryConvertBase(true, false); };

        if (!string.IsNullOrEmpty(_currentValue) && _currentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            _hexRadio.IsChecked = true;
        else
            _decRadio.IsChecked = true;

        pnl.Children.Add(_hexRadio); pnl.Children.Add(_decRadio);
        WpfG.SetRow(pnl, r); g.Children.Add(pnl);

        _statusLabel = new WpfTxt
        {
            Foreground = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x55, 0x55, 0x55)),
            FontSize = 11, Margin = new WpfTh(0, 28, 0, 0),
            Visibility = WinVis.Collapsed
        };
        WpfG.SetRow(_statusLabel, r); g.Children.Add(_statusLabel);
    }

    private void BuildButtonsRow(WpfG g, int r)
    {
        var pnl = new WpfSP
        {
            Orientation = WinOrientation.Horizontal,
            HorizontalAlignment = WinHA.Right,
            Margin = new WpfTh(0, 8, 0, 0)
        };

        var ok = new WpfB
        {
            Content = "确定", Width = 72, Height = 32, Margin = new WpfTh(0, 0, 8, 0), IsDefault = true,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x00, 0xA6, 0x5A)),
            Foreground = WpfM.Brushes.White,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x00, 0xA6, 0x5A))
        };
        ok.Click += (_, _) => { if (ValidateAndCommit()) { DialogResult = true; Close(); } };

        var cancel = new WpfB
        {
            Content = "取消", Width = 72, Height = 32, IsCancel = true,
            Background = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x20, 0x20, 0x20)),
            Foreground = WpfM.Brushes.LightGray,
            BorderBrush = new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x4B, 0x55, 0x63))
        };
        cancel.Click += (_, _) => Close();

        pnl.Children.Add(ok); pnl.Children.Add(cancel);
        WpfG.SetRow(pnl, r); g.Children.Add(pnl);
    }

    private void TryConvertBase(bool fromHex, bool toHex)
    {
        _suppressBaseChange = true;
        try
        {
            var text = _dataBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (IsDwordType())
            {
                if (fromHex)
                {
                    if (uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    { _dataBox.Text = v.ToString(); ShowStatus("", false); }
                    else ShowStatus("无效的十六进制值", true);
                }
                else
                {
                    if (uint.TryParse(text, out var v))
                    { _dataBox.Text = $"0x{v:X8}"; ShowStatus("", false); }
                    else ShowStatus("无效的十进制值", true);
                }
            }
            else if (IsQwordType())
            {
                if (fromHex)
                {
                    if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    { _dataBox.Text = v.ToString(); ShowStatus("", false); }
                    else ShowStatus("无效的十六进制值", true);
                }
                else
                {
                    if (ulong.TryParse(text, out var v))
                    { _dataBox.Text = $"0x{v:X16}"; ShowStatus("", false); }
                    else ShowStatus("无效的十进制值", true);
                }
            }
        }
        finally { _suppressBaseChange = false; }
    }

    private bool ValidateAndCommit()
    {
        var data = _dataBox.Text.Trim();
        if (IsNumericTypeFor(_type) && _decRadio.IsChecked == true)
        {
            try
            {
                if (IsDwordType()) { data = $"0x{Convert.ToUInt32(data):X8}"; }
                else if (IsQwordType()) { data = $"0x{Convert.ToUInt64(data):X16}"; }
            }
            catch { ShowStatus("请输入有效的十进制数值", true); return false; }
        }
        ValueName = _nameBox.Text.Trim();
        ValueData = data;
        return true;
    }

    private void ShowStatus(string msg, bool isError)
    {
        if (_statusLabel == null) return;
        if (string.IsNullOrEmpty(msg))
            _statusLabel.Visibility = WinVis.Collapsed;
        else
        {
            _statusLabel.Visibility = WinVis.Visible;
            _statusLabel.Foreground = isError
                ? new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0xD4, 0x2E, 0x45))
                : new WpfM.SolidColorBrush(WpfM.Color.FromRgb(0x88, 0x88, 0x88));
            _statusLabel.Text = msg;
        }
    }

    private static bool IsNumericTypeFor(string t) => IsDwordTypeFor(t) || IsQwordTypeFor(t);
    private bool IsDwordType() => IsDwordTypeFor(_type);
    private bool IsQwordType() => IsQwordTypeFor(_type);
    private static bool IsDwordTypeFor(string t) => t is "REG_DWORD" or "REG_DWORD_LITTLE_ENDIAN" or "REG_DWORD_BIG_ENDIAN";
    private static bool IsQwordTypeFor(string t) => t is "REG_QWORD" or "REG_QWORD_LITTLE_ENDIAN";
}
