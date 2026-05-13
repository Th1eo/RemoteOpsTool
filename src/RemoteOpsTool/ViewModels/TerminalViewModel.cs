using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly INetworkService _networkService;
    private const int PsExecUploadChunkSize = 1500;

    [ObservableProperty] private string _commandText = string.Empty;
    [ObservableProperty] private bool _isInteractiveMode;
    [ObservableProperty] private string _selectedSession = "";
    [ObservableProperty] private bool _hasSessions;

    public ObservableCollection<string> AvailableSessions { get; } = [];

    public TerminalViewModel(MainViewModel main, ILogService logService,
        IPsExecService psExecService, INetworkService networkService)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
        _networkService = networkService;
    }

    partial void OnIsInteractiveModeChanged(bool value)
    {
        if (value)
            _ = RefreshSessionsAsync();
    }

    public async Task RefreshSessionsAsync()
    {
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            var sessions = await _networkService.GetUserSessionsAsync(host, cred.UserName, password ?? string.Empty);

            AvailableSessions.Clear();
            foreach (var s in sessions.Where(s => !string.IsNullOrWhiteSpace(s.Username)))
            {
                var display = $"{s.Username} (会话{s.SessionId}, {s.State})";
                if (!AvailableSessions.Contains(display))
                    AvailableSessions.Add(display);
            }
            HasSessions = AvailableSessions.Count > 0;
            if (HasSessions && string.IsNullOrEmpty(SelectedSession))
                SelectedSession = AvailableSessions.FirstOrDefault(s => s.Contains("Active") || s.Contains("运行中")) ?? AvailableSessions[0];
        }
        catch { }
    }

    [RelayCommand]
    private async Task ExecuteCommandAsync()
    {
        var command = CommandText.Trim();
        if (string.IsNullOrEmpty(command)) return;

        CommandText = string.Empty;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
        {
            _logService.Warn("请先选择凭据。");
            return;
        }

        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        if (IsInteractiveMode && !string.IsNullOrEmpty(SelectedSession))
        {
            var sessionId = ParseSessionId(SelectedSession);
            if (sessionId < 0)
            {
                _logService.Warn("无法解析会话ID，使用默认会话。");
                sessionId = 1;
            }

            _logService.Info($"交互执行到会话 {sessionId}: {command}");

            if (HostHelper.IsLocalHost(host))
                _psExecService.ExecuteInteractiveLocal(command);
            else
                await _psExecService.ExecuteInteractiveRemoteAsync(
                    host,
                    cred.UserName,
                    password ?? string.Empty,
                    command,
                    sessionId: sessionId);
        }
        else
        {
            _logService.Info($"远程执行: {command}");
            await _psExecService.ExecuteWithOutputAsync(host, cred.UserName, password ?? string.Empty,
                command, line => _logService.Info(line));
        }
    }

    private static int ParseSessionId(string sessionDisplay)
    {
        // Format: "username (会话3, Active)"
        var start = sessionDisplay.IndexOf("会话", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return -1;
        var numStart = start + 2;
        var numEnd = sessionDisplay.IndexOfAny([')', ','], numStart);
        if (numEnd < 0) numEnd = sessionDisplay.Length;
        if (int.TryParse(sessionDisplay[numStart..numEnd], out var id))
            return id;
        return -1;
    }

    [RelayCommand]
    private async Task LoadScriptAsync()
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host))
        {
            _logService.Warn("请先输入目标主机名。");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "脚本文件 (*.bat;*.ps1;*.cmd)|*.bat;*.ps1;*.cmd|所有文件 (*.*)|*.*",
            Title = "选择脚本文件"
        };

        if (WindowHelper.RunWithStateGuard(
            System.Windows.Application.Current.MainWindow,
            () => dialog.ShowDialog()) != true) return;

        await LoadScriptFileAsync(dialog.FileName);
    }

    public async Task LoadScriptFileAsync(string localPath)
    {
        var host = _main.GetTargetHost();
        if (string.IsNullOrEmpty(host))
        {
            _logService.Warn("请先输入目标主机名。");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
            {
                _logService.Warn("脚本文件不存在。");
                return;
            }

            var ext = Path.GetExtension(localPath);
            if (!new[] { ".bat", ".cmd", ".ps1" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                _logService.Warn("仅支持加载 .bat、.cmd、.ps1 脚本文件。");
                return;
            }

            var fileName = Path.GetFileName(localPath);
            var remoteScriptPath = await PrepareScriptOnTargetAsync(host, localPath, fileName);

            if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                CommandText = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{remoteScriptPath}\"";
            else
                CommandText = $"call \"{remoteScriptPath}\"";

            _logService.Info($"执行命令已生成，点击「执行」或按 Enter 发送到目标主机。");
        }
        catch (Exception ex)
        {
            _logService.Error($"脚本上传失败: {ex.Message}");
        }
    }

    private async Task<string> PrepareScriptOnTargetAsync(string host, string localPath, string fileName)
    {
        if (HostHelper.IsLocalHost(host))
        {
            _logService.Info($"已加载本地脚本: {localPath}");
            return localPath;
        }

        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
            throw new InvalidOperationException("请先选择凭据。");

        var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
        var shareUpload = await TryUploadScriptViaShareAsync(host, cred.UserName, password, localPath, fileName);
        if (!string.IsNullOrWhiteSpace(shareUpload))
            return shareUpload;

        return await UploadScriptViaPsExecAsync(host, cred.UserName, password, localPath, fileName);
    }

    private async Task<string?> TryUploadScriptViaShareAsync(
        string host,
        string username,
        string password,
        string localPath,
        string fileName)
    {
        var targets = new[]
        {
            new ScriptUploadTarget(
                NetworkPathHelper.BuildAdminShare(host, "C"),
                $@"{NetworkPathHelper.BuildAdminShare(host, "C")}\Temp",
                $@"C:\Temp\{fileName}"),
            new ScriptUploadTarget(
                $@"\\{host}\ADMIN$",
                $@"\\{host}\ADMIN$\Temp",
                $@"C:\Windows\Temp\{fileName}")
        };

        var errors = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                var result = await Task.Run(() =>
                {
                    var connection = NetworkShareCredentialHelper.EnsureConnection(target.ShareRoot, username, password);
                    if (!connection.Success)
                        return (Success: false, Message: connection.Message);

                    Directory.CreateDirectory(target.ShareDirectory);
                    File.Copy(localPath, Path.Combine(target.ShareDirectory, fileName), true);
                    return (Success: true, Message: "OK");
                });

                if (result.Success)
                {
                    _logService.Info($"脚本已上传到目标主机: {target.RemoteScriptPath}");
                    return target.RemoteScriptPath;
                }

                errors.Add($"{target.ShareRoot}: {result.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"{target.ShareRoot}: {ex.Message}");
            }
        }

        _logService.Warn($"SMB 脚本上传失败，改用 PsExec 写入脚本: {string.Join("；", errors)}");
        return null;
    }

    private async Task<string> UploadScriptViaPsExecAsync(
        string host,
        string username,
        string password,
        string localPath,
        string fileName)
    {
        var remoteScriptPath = $@"C:\Temp\{fileName}";
        var remoteBase64Path = $@"C:\Temp\{fileName}.b64";
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(localPath));

        _logService.Info("正在通过 PsExec 写入脚本到目标主机...");
        await RunPsExecUploadStepAsync(host, username, password,
            $"New-Item -ItemType Directory -Path 'C:\\Temp' -Force | Out-Null; " +
            $"Set-Content -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Value '' -NoNewline -Encoding ascii");

        for (var offset = 0; offset < base64.Length; offset += PsExecUploadChunkSize)
        {
            var chunk = base64.Substring(offset, Math.Min(PsExecUploadChunkSize, base64.Length - offset));
            await RunPsExecUploadStepAsync(host, username, password,
                $"Add-Content -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Value {PowerShellLiteral(chunk)} -NoNewline -Encoding ascii");
        }

        await RunPsExecUploadStepAsync(host, username, password,
            $"$b = Get-Content -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Raw -Encoding ascii; " +
            $"[IO.File]::WriteAllBytes({PowerShellLiteral(remoteScriptPath)}, [Convert]::FromBase64String($b)); " +
            $"Remove-Item -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Force -ErrorAction SilentlyContinue");

        _logService.Info($"脚本已通过 PsExec 写入目标主机: {remoteScriptPath}");
        return remoteScriptPath;
    }

    private async Task RunPsExecUploadStepAsync(string host, string username, string password, string script)
    {
        var command = EncodePowerShellCommand(script);
        var result = await _psExecService.ExecuteAsync(
            host,
            username,
            password,
            command,
            silent: true,
            wrapCmd: false);

        if (!result.Success)
            throw new InvalidOperationException($"PsExec 写入脚本失败: {result.StdErr}".Trim());
    }

    private static string EncodePowerShellCommand(string script)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(script);
        return $"powershell -NoProfile -EncodedCommand {Convert.ToBase64String(bytes)}";
    }

    private static string PowerShellLiteral(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    private sealed record ScriptUploadTarget(string ShareRoot, string ShareDirectory, string RemoteScriptPath);
}
