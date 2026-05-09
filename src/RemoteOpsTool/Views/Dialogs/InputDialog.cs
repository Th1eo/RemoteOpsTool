using System.Windows;

namespace RemoteOpsTool.Views.Dialogs;

public class InputDialog : Window
{
    public string Result { get; private set; } = "";

    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 400; Height = 140;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Transparent;

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        var tb = new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
        var box = new System.Windows.Controls.TextBox { Height = 28, Name = "InputBox" };
        System.Windows.Controls.Grid.SetRow(tb, 0);
        System.Windows.Controls.Grid.SetRow(box, 1);
        grid.Children.Add(tb); grid.Children.Add(box);

        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Result = box.Text; DialogResult = true; Close(); } };

        Content = grid;
    }
}
