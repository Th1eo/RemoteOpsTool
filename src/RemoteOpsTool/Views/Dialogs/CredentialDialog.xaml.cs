using System.Windows;

namespace RemoteOpsTool.Views.Dialogs;

public partial class CredentialDialog : Window
{
    private bool _passwordVisible;

    public CredentialDialog()
    {
        InitializeComponent();
        HiddenPasswordBox.PasswordChanged += (_, _) =>
        {
            if (_vm != null && !_passwordVisible)
                _vm.NewPassword = HiddenPasswordBox.Password;
        };
    }

    private ViewModels.CredentialViewModel? _vm;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DataContextProperty && e.NewValue is ViewModels.CredentialViewModel vm)
            _vm = vm;
    }

    private void EyeBtn_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        if (_passwordVisible)
        {
            VisiblePasswordBox.Text = HiddenPasswordBox.Password;
            if (_vm != null) _vm.NewPassword = HiddenPasswordBox.Password;
            HiddenPasswordBox.Visibility = Visibility.Collapsed;
            VisiblePasswordBox.Visibility = Visibility.Visible;
            EyeIcon.Text = "◉";
        }
        else
        {
            HiddenPasswordBox.Password = VisiblePasswordBox.Text;
            if (_vm != null) _vm.NewPassword = VisiblePasswordBox.Text;
            HiddenPasswordBox.Visibility = Visibility.Visible;
            VisiblePasswordBox.Visibility = Visibility.Collapsed;
            EyeIcon.Text = "👁";
        }
    }
}
