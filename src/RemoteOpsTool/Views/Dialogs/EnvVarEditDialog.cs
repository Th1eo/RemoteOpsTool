namespace RemoteOpsTool.Views.Dialogs;

public class EnvVarEditDialog : System.Windows.Window
{
    public string VariableName { get; private set; }
    public string VariableValue { get; private set; }
    public bool Confirmed { get; private set; }

    public EnvVarEditDialog(string name, string value)
    {
        VariableName = name;
        VariableValue = value;

        Title = "编辑环境变量";
        Width = 560; Height = 310;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
        ResizeMode = System.Windows.ResizeMode.CanResize;
        MinWidth = 400; MinHeight = 250;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x10));
        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xDC, 0xDC));
        FontFamily = System.Windows.Application.Current.TryFindResource("UiFont") as System.Windows.Media.FontFamily
            ?? new System.Windows.Media.FontFamily("JetBrains Mono, Source Han Sans SC");

        var grid = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        var namePanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 8) };
        namePanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "变量名:", FontSize = 13, Foreground = System.Windows.Media.Brushes.Gray, Margin = new System.Windows.Thickness(0, 0, 0, 4) });
        var nameBox = new System.Windows.Controls.TextBox
        {
            Text = name, Height = 28,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x16, 0x16)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4B, 0x55, 0x63))
        };
        namePanel.Children.Add(nameBox);
        System.Windows.Controls.Grid.SetRow(namePanel, 0);

        var valLabel = new System.Windows.Controls.TextBlock { Text = "变量值:", FontSize = 13, Foreground = System.Windows.Media.Brushes.Gray, Margin = new System.Windows.Thickness(0, 8, 0, 4) };
        System.Windows.Controls.Grid.SetRow(valLabel, 1);

        var valBox = new System.Windows.Controls.TextBox
        {
            Text = value, AcceptsReturn = true, TextWrapping = System.Windows.TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Top,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(6, 4, 6, 4),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x16, 0x16)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4B, 0x55, 0x63)),
            FontSize = 13
        };
        System.Windows.Controls.Grid.SetRow(valBox, 2);

        var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "取消", Width = 80, Height = 34, Margin = new System.Windows.Thickness(0, 0, 8, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20)), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xDC, 0xDC)), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4B, 0x55, 0x63)) };
        cancelBtn.Click += (_, _) => Close();
        var okBtn = new System.Windows.Controls.Button { Content = "确定", Width = 80, Height = 34, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xA6, 0x5A)), Foreground = System.Windows.Media.Brushes.White, BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xA6, 0x5A)) };
        okBtn.Click += (_, _) => { VariableName = nameBox.Text; VariableValue = valBox.Text; Confirmed = true; DialogResult = true; Close(); };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        System.Windows.Controls.Grid.SetRow(btnPanel, 3);

        valBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                VariableName = nameBox.Text; VariableValue = valBox.Text; Confirmed = true; DialogResult = true; Close();
            }
        };

        grid.Children.Add(namePanel);
        grid.Children.Add(valLabel);
        grid.Children.Add(valBox);
        grid.Children.Add(btnPanel);
        Content = grid;

        Loaded += (_, _) => { valBox.Focus(); valBox.SelectAll(); };
    }
}
