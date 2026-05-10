using System.Windows;

namespace RemoteOpsTool.Views.Dialogs;

public class InputDialog : Window
{
    public string Result { get; private set; } = "";

    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 400; Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x17, 0x15));
        Owner = System.Windows.Application.Current.MainWindow;

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(14, 12, 14, 12) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        var tb = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Foreground = System.Windows.Media.Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var box = new System.Windows.Controls.TextBox
        {
            Height = 28,
            Name = "InputBox",
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1E, 0x1B)),
            Foreground = System.Windows.Media.Brushes.LightGray,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0x6A, 0x64)),
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 10, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(tb, 0);
        System.Windows.Controls.Grid.SetRow(box, 1);
        grid.Children.Add(tb); grid.Children.Add(box);

        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Result = box.Text; DialogResult = true; Close(); } };

        Content = grid;
    }
}
