using System.Windows;
using RemoteOpsTool.ViewModels.Dialogs;

namespace RemoteOpsTool.Views.Dialogs;

public partial class ServicePropertiesDialog : Window
{
    public ServicePropertiesDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ServicePropertiesViewModel vm)
                vm.CloseRequested += OnCloseRequested;
        };
        Closed += (_, _) =>
        {
            if (DataContext is ServicePropertiesViewModel vm)
                vm.CloseRequested -= OnCloseRequested;
        };
    }

    private void OnCloseRequested()
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 用户点击“应用/确定”时，先把 PasswordBox 的输入交给 ViewModel，
    /// 而不是让 ViewModel 去全局查找窗口，避免多窗口时取错密码。
    /// </summary>
    private bool TryCaptureCredentials()
    {
        if (DataContext is not ServicePropertiesViewModel vm) return true;

        var password = LogOnPassword.Password;
        var confirm = LogOnConfirm.Password;
        if (vm.TryCaptureUserInput(password, confirm, out var error) == false)
        {
            MessageBox.Show(error, "服务属性", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureCredentials()) return;
        if (DataContext is ServicePropertiesViewModel vm && vm.ApplyCommand.CanExecute(null))
            vm.ApplyCommand.Execute(null);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCaptureCredentials()) return;
        if (DataContext is ServicePropertiesViewModel vm && vm.OkCommand.CanExecute(null))
            vm.OkCommand.Execute(null);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
