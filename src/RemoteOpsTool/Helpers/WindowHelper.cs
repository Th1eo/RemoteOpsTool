using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace RemoteOpsTool.Helpers;

public static class WindowHelper
{
    public static bool? ShowDialogSafe(this Window dialog, Window owner)
    {
        dialog.Owner = owner;
        return RunWithStateGuard(owner, () => dialog.ShowDialog());
    }

    public static T RunWithStateGuard<T>(Window owner, Func<T> action)
    {
        var prevState = owner.WindowState;
        var result = action();
        owner.WindowState = prevState;
        owner.Focus();
        Keyboard.ClearFocus();
        return result;
    }

    public static void HandleWindowClosing(Window window, CancelEventArgs e)
    {
        if (window.Owner != null)
        {
            window.Owner.Activate();
            if (window.Owner.WindowState == WindowState.Minimized)
                window.Owner.WindowState = WindowState.Normal;
            window.Owner.Focus();
            Keyboard.ClearFocus();
        }
    }
}
