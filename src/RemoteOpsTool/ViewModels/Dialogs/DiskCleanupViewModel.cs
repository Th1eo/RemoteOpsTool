using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class DiskCleanupViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IFileDiskService _fileDiskService;
    private readonly ISettingsService _settingsService;

    [ObservableProperty]
    private string _customDirectories = string.Empty;

    [ObservableProperty]
    private bool _isCleaning;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private int _progressMaximum = 1;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _deleteDirectory;

    public ObservableCollection<CleanupItem> Items { get; } = [];

    public DiskCleanupViewModel(MainViewModel main, ILogService logService, IFileDiskService fileDiskService, ISettingsService settingsService)
    {
        _main = main;
        _logService = logService;
        _fileDiskService = fileDiskService;
        _settingsService = settingsService;

        Items.Add(new CleanupItem { Path = @"C:\Windows\Temp", Description = "Windows Temp", IsChecked = true });
        Items.Add(new CleanupItem { Path = @"C:\Windows\Prefetch", Description = "Prefetch", IsChecked = true });
        Items.Add(new CleanupItem { Path = @"C:\Windows\SoftwareDistribution\Download", Description = "Windows Update Cache", IsChecked = true });
    }

    [RelayCommand]
    private async Task CleanupAsync()
    {
        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;

        var password = _main.Connection.CredentialService.DecryptPassword(cred);
        var targets = Items.Where(i => i.IsChecked)
            .Select(i => new DiskCleanupTarget(i.Path, DeleteDirectory: false))
            .ToList();

        if (!string.IsNullOrWhiteSpace(CustomDirectories))
        {
            var custom = CustomDirectories
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => d.Length > 0);
            targets.AddRange(custom.Select(path => new DiskCleanupTarget(path, DeleteDirectory)));
        }

        // Cleanup targets are one-shot input. Clear the editor immediately so the
        // previous paths do not remain visible while cleanup is running or when the
        // dialog is opened again.
        CustomDirectories = string.Empty;
        _settingsService.Settings.CustomCleanupDirectories = string.Empty;

        if (targets.Count == 0) return;

        IsCleaning = true;
        ProgressValue = 0;
        ProgressMaximum = targets.Select(target => target.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        ProgressText = $"正在准备清理 {ProgressMaximum} 个目标...";

        try
        {
            try
            {
                await _settingsService.SaveAsync();
            }
            catch (Exception ex)
            {
                // A settings persistence failure must not prevent the requested cleanup.
                _logService.Warn($"清空自定义清理目录保存失败，将继续执行清理: {ex.Message}");
            }

            var progress = new Progress<DiskCleanupProgress>(update =>
            {
                ProgressValue = update.Completed;
                ProgressMaximum = Math.Max(1, update.Total);
                ProgressText = update.Message;
            });
            var result = await _fileDiskService.CleanupDisksAsync(
                host, cred.UserName, password ?? string.Empty, targets, progress);

            if (result.Success)
            {
                ProgressText = $"清理完成，已验证 {result.SucceededCount} 个目标。";
                System.Windows.MessageBox.Show(
                    $"清理完成，已确认 {result.SucceededCount} 个目标已删除或清空。",
                    "清理完成", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            else
            {
                var failedPaths = string.Join(Environment.NewLine,
                    result.Targets.Where(target => !target.Success).Select(target => $"• {target.Path}"));
                ProgressText = $"清理结束：成功 {result.SucceededCount}，失败 {result.FailedCount}。";
                System.Windows.MessageBox.Show(
                    $"清理结束，但有 {result.FailedCount} 个目标未能确认删除：{Environment.NewLine}{failedPaths}",
                    "清理未完全完成", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "清理已取消。";
        }
        catch (Exception ex)
        {
            ProgressText = "清理执行失败。";
            _logService.Error($"磁盘清理异常: {ex.Message}");
            System.Windows.MessageBox.Show(ex.Message, "清理失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsCleaning = false;
        }
    }

    public partial class CleanupItem : ObservableObject
    {
        [ObservableProperty] private string _path = string.Empty;
        [ObservableProperty] private string _description = string.Empty;
        [ObservableProperty] private bool _isChecked;
    }
}
