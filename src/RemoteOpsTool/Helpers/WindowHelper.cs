using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RemoteOpsTool.Helpers;

public static class WindowHelper
{
    private const uint MonitorDefaultToNearest = 2;
    private const double BaseDpi = 96.0;
    private const double WindowSafetyMargin = 12.0;

    private static bool _responsiveSizingRegistered;

    /// <summary>
    /// Applies a common DPI-aware safety net to every WPF window in the application.
    /// WPF Width/Height values are device-independent pixels (DIPs), not physical pixels.
    /// </summary>
    public static void RegisterResponsiveSizing()
    {
        if (_responsiveSizingRegistered)
            return;

        _responsiveSizingRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(WindowLoaded));
    }

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

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || window.WindowState == WindowState.Maximized)
            return;

        // Loaded is raised after the HWND has been created, so the monitor and its
        // effective DPI are available even for dialogs created from code.
        ConstrainToMonitorWorkArea(window);
    }

    private static void ConstrainToMonitorWorkArea(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var hwnd = source is HwndSource hwndSource
            ? hwndSource.Handle
            : new WindowInteropHelper(window).Handle;

        if (hwnd == IntPtr.Zero)
            return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;

        // Monitor work-area coordinates are physical pixels. Convert them to WPF
        // DIPs using the DPI of the monitor containing this window.
        var scale = dpi / BaseDpi;
        var workArea = new Rect(
            monitorInfo.rcWork.Left / scale,
            monitorInfo.rcWork.Top / scale,
            (monitorInfo.rcWork.Right - monitorInfo.rcWork.Left) / scale,
            (monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top) / scale);

        var maxWidth = Math.Max(200, workArea.Width - WindowSafetyMargin * 2);
        var maxHeight = Math.Max(120, workArea.Height - WindowSafetyMargin * 2);

        // A minimum larger than the monitor would make WPF ignore the maximum
        // and still produce an off-screen window. Lower it only when necessary.
        if (window.MinWidth > maxWidth)
            window.MinWidth = maxWidth;
        if (window.MinHeight > maxHeight)
            window.MinHeight = maxHeight;

        window.MaxWidth = Math.Min(window.MaxWidth, maxWidth);
        window.MaxHeight = Math.Min(window.MaxHeight, maxHeight);

        if (!double.IsNaN(window.Width) && window.Width > maxWidth)
            window.Width = maxWidth;
        if (!double.IsNaN(window.Height) && window.Height > maxHeight)
            window.Height = maxHeight;

        // CenterScreen/CenterOwner uses the original requested size. Correct a
        // potentially negative/out-of-bounds position after a size was clamped.
        if (!double.IsNaN(window.Left) && !double.IsNaN(window.Top))
        {
            var width = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
            var height = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;

            if (width > 0 && height > 0)
            {
                window.Left = Math.Clamp(window.Left, workArea.Left, workArea.Right - width);
                window.Top = Math.Clamp(window.Top, workArea.Top, workArea.Bottom - height);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
