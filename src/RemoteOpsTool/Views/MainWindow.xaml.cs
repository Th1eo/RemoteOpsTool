using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.IO;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Views;

public partial class MainWindow : Window
{
    private ViewModels.MainViewModel _vm = null!;
    private readonly IFileDiskService _fileDiskService;
    private readonly INetworkService _networkService;
    private System.Windows.Threading.DispatcherTimer? _diskTimer;
    private System.Windows.Threading.DispatcherTimer? _hostInputTimer;
    private string _lastHost = string.Empty;

    public MainWindow(ViewModels.MainViewModel viewModel, IFileDiskService fileDiskService, INetworkService networkService)
    {
        InitializeComponent();

        _vm = viewModel;
        _fileDiskService = fileDiskService;
        _networkService = networkService;

        // Handle manual reconnect from UI
        _vm.ReconnectRequested += async () =>
        {
            if (_hostInputTimer != null)
            {
                _hostInputTimer.Stop();
                _hostInputTimer = null;
            }
            await TestHostConnectivityAsync();
        };

        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RemoteOpsTool.Views.Resources.Tools.ico");
            if (stream != null)
            {
                var decoder = new IconBitmapDecoder(stream,
                    BitmapCreateOptions.None, BitmapCacheOption.Default);
                Icon = decoder.Frames[0];
            }
        }
        catch { }

        DataContext = viewModel;

        _vm.Log.EntryAdded += AppendLogEntry;
        _vm.Log.LogCleared += () => Dispatcher.Invoke(() => LogBox.Document.Blocks.Clear());
        _vm.Log.LogRebuilt += RebuildLog;

        Loaded += (_, _) =>
        {
            LoadSvgIcon();

            // Sync any entries logged before window was shown (env check etc.)
            foreach (var entry in _vm.Log.Entries)
                AppendBlock(entry);

            _vm.StatusBar.UpdateTime();
            var clockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            clockTimer.Tick += (_, _) => _vm.StatusBar.UpdateTime();
            clockTimer.Start();

            var diskTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            diskTimer.Tick += async (_, _) => await RefreshDiskInfoAsync();
            diskTimer.Start();
            _diskTimer = diskTimer;

            _ = RefreshDiskInfoAsync();

            _vm.StatusBar.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(_vm.StatusBar.IsPinging) ||
                    e.PropertyName == nameof(_vm.StatusBar.ConnectionDotColor))
                {
                    Dispatcher.Invoke(() => UpdateStatusDot());
                }
            };
        };
    }

    private System.Windows.Threading.DispatcherTimer? _dotPulseTimer;

    private void UpdateStatusDot()
    {
        var colorStr = _vm.StatusBar.IsPinging ? "#4ECB71" : _vm.StatusBar.ConnectionDotColor;
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
        StatusDot.Fill = new SolidColorBrush(color);

        if (_vm.StatusBar.IsPinging && _dotPulseTimer == null)
        {
            _dotPulseTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            var dim = true;
            _dotPulseTimer.Tick += (_, _) =>
            {
                dim = !dim;
                StatusDot.Opacity = dim ? 0.4 : 1.0;
            };
            _dotPulseTimer.Start();
        }
        else if (!_vm.StatusBar.IsPinging && _dotPulseTimer != null)
        {
            _dotPulseTimer.Stop();
            _dotPulseTimer = null;
            StatusDot.Opacity = 1.0;
        }
    }

    private static readonly System.Windows.Media.Brush InfoBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 203, 113));
    private static readonly System.Windows.Media.Brush WarnBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0));
    private static readonly System.Windows.Media.Brush ErrorBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 107));

    private static System.Windows.Media.Brush BrushForLevel(LogLevel level) => level switch
    {
        LogLevel.Warn => WarnBrush,
        LogLevel.Error => ErrorBrush,
        _ => InfoBrush
    };

    private void AppendLogEntry(LogEntry entry)
    {
        Dispatcher.Invoke(() => AppendBlock(entry));
    }

    private void AppendBlock(LogEntry entry)
    {
        var p = new Paragraph { Margin = new Thickness(8, 0, 8, 0), LineHeight = 1 };
        p.Inlines.Add(new Run(entry.DisplayText) { Foreground = BrushForLevel(entry.Level) });
        LogBox.Document.Blocks.Add(p);
        LogBox.ScrollToEnd();

        if (LogBox.Document.Blocks.Count > 10000)
            LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);
    }

    private void RebuildLog()
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.Document.Blocks.Clear();
            foreach (var entry in _vm.Log.Entries)
            {
                var p = new Paragraph { Margin = new Thickness(8, 0, 8, 0), LineHeight = 1 };
                p.Inlines.Add(new Run(entry.DisplayText) { Foreground = BrushForLevel(entry.Level) });
                LogBox.Document.Blocks.Add(p);
            }
        });
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = LogBox.Selection.Text;
        if (!string.IsNullOrEmpty(selected))
            System.Windows.Clipboard.SetText(selected);
        else
        {
            var range = new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
            System.Windows.Clipboard.SetText(range.Text);
        }
    }

    private void LogCopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selected = LogBox.Selection.Text;
        if (!string.IsNullOrEmpty(selected))
            System.Windows.Clipboard.SetText(selected);
    }

    private void LogSelectAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LogBox.SelectAll();
    }

    private void LogClearMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _vm.Log.ClearLogCommand.Execute(null);
    }

    private void LoadSvgIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RemoteOpsTool.Views.Resources.Tools.svg");
            if (stream != null)
            {
                var svgImage = SvgImageHelper.LoadSvg(stream, 88, 88);
                if (svgImage != null)
                    AppIcon.Source = svgImage;
            }
        }
        catch { }
    }

    private void TargetHostBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _hostInputTimer?.Stop();
        _hostInputTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _hostInputTimer.Tick += async (_, _) =>
        {
            _hostInputTimer.Stop();
            await TestHostConnectivityAsync();
        };
        _hostInputTimer.Start();
    }

    private async void TargetHostBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _hostInputTimer?.Stop();
        await TestHostConnectivityAsync();
    }

    private async Task TestHostConnectivityAsync()
    {
        var host = _vm.GetTargetHost();
        if (string.IsNullOrWhiteSpace(host)) return;

        if (!string.Equals(host, _lastHost, StringComparison.OrdinalIgnoreCase))
        {
            _lastHost = host;
            _vm.StatusBar.HostName = host;
            _vm.StatusBar.CDriveInfo = "--";
            _vm.StatusBar.DDriveInfo = "--";
        }

        _vm.ResetFailureCount();

        _vm.Log.LogService.Info($"正在检测 {host} 连通性...");

        try
        {
            var result = await _networkService.PingAsync(host);
            if (result.Success)
            {
                _vm.Log.LogService.Info($"{host} 连通正常，远程操作已启用。");
                _vm.MarkConnectionSuccess();
                if (_diskTimer != null && !_diskTimer.IsEnabled)
                    _diskTimer.Start();
            }
            else
            {
                _vm.Log.LogService.Warn($"{host} Ping 无响应。{result.Output}");
                _vm.MarkConnectionFailure();
            }
            _vm.StatusBar.HostName = host;
        }
        catch (Exception ex)
        {
            _vm.Log.LogService.Error($"检测 {host} 失败: {ex.Message}");
            _vm.MarkConnectionFailure();
        }

        await RefreshDiskInfoAsync();
    }

    private void TerminalInputBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                return;

            e.Handled = true;
            _vm.Terminal.ExecuteCommandCommand.Execute(null);
        }
    }

    private async Task RefreshDiskInfoAsync()
    {
        if (_vm.IsConnectionLocked)
        {
            _diskTimer?.Stop();
            return;
        }

        if (!_vm.IsConnected) return;

        try
        {
            var host = _vm.GetTargetHost();
            if (string.IsNullOrEmpty(host)) return;

            var cred = _vm.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;

            _vm.StatusBar.HostName = host;

            var pwd = _vm.Connection.CredentialService.DecryptPassword(cred);
            var disks = await _fileDiskService.GetDiskInfoAsync(host, cred.UserName, pwd ?? string.Empty, silent: true);

            if (disks.Count > 0)
            {
                var cDisk = disks.FirstOrDefault(d => d.DeviceId == "C:");
                var dDisk = disks.FirstOrDefault(d => d.DeviceId == "D:");
                _vm.StatusBar.CDriveInfo = cDisk != null ? $"{cDisk.FreeGB:F1} GB / {cDisk.SizeGB:F1} GB" : "--";
                _vm.StatusBar.DDriveInfo = dDisk != null ? $"{dDisk.FreeGB:F1} GB / {dDisk.SizeGB:F1} GB" : "--";
            }
        }
        catch
        {
        }
    }
}
