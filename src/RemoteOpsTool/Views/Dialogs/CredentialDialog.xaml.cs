using System.ComponentModel;
using System.Windows;

namespace RemoteOpsTool.Views.Dialogs;

public partial class CredentialDialog : Window
{
    private bool _passwordVisible;
    private ViewModels.CredentialViewModel? _vm;

    public CredentialDialog()
    {
        InitializeComponent();
        Closed += (_, _) => AttachViewModel(null);
        HiddenPasswordBox.PasswordChanged += (_, _) =>
        {
            if (_vm != null && !_passwordVisible)
                _vm.NewPassword = HiddenPasswordBox.Password;
        };
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == DataContextProperty)
            AttachViewModel(e.NewValue as ViewModels.CredentialViewModel);
    }

    private void AttachViewModel(ViewModels.CredentialViewModel? vm)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = vm;

        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.CredentialViewModel.NewPassword) ||
            !string.IsNullOrEmpty(_vm?.NewPassword))
        {
            return;
        }

        if (!string.IsNullOrEmpty(HiddenPasswordBox.Password))
            HiddenPasswordBox.Password = string.Empty;
        if (!string.IsNullOrEmpty(VisiblePasswordBox.Text))
            VisiblePasswordBox.Text = string.Empty;
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
