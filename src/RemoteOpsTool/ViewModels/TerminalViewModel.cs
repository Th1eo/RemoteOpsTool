using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ILogService _logService;
    private readonly IPsExecService _psExecService;
    private readonly IRemoteExecutionService _execution;
    private readonly INetworkService _networkService;
    private readonly ScriptShareUploader _scriptShareUploader;
    private const int UploadChunkSize = 1500;

    internal delegate Task<string?> ScriptShareUploader(
        string host,
        string username,
        string password,
        string localPath,
        string fileName);

    [ObservableProperty] private string _commandText = string.Empty;
    [ObservableProperty] private bool _isInteractiveMode;
    [ObservableProperty] private CommandShell _selectedShell = CommandShell.PowerShell;
    [ObservableProperty] private string _selectedSession = "";
    [ObservableProperty] private bool _hasSessions;

    public ObservableCollection<string> AvailableSessions { get; } = [];
    public IReadOnlyList<CommandShell> ShellOptions { get; } = [CommandShell.PowerShell, CommandShell.Cmd, CommandShell.Direct];
    private CancellationTokenSource? _executeCts;

    public TerminalViewModel(MainViewModel main, ILogService logService,
        IPsExecService psExecService, IRemoteExecutionService execution, INetworkService networkService)
        : this(main, logService, psExecService, execution, networkService, null)
    {
    }

    internal TerminalViewModel(MainViewModel main, ILogService logService,
        IPsExecService psExecService, IRemoteExecutionService execution, INetworkService networkService,
        ScriptShareUploader? scriptShareUploader)
    {
        _main = main;
        _logService = logService;
        _psExecService = psExecService;
        _execution = execution;
        _networkService = networkService;
        _scriptShareUploader = scriptShareUploader ?? TryUploadScriptViaShareAsync;
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

            var session = await _execution.CreateSessionAsync(host, cred.UserName, password ?? string.Empty);
            var sessions = await _networkService.GetUserSessionsWithSessionAsync(
                session, host, cred.UserName, password ?? string.Empty);

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
    private Task ExecuteCommandAsync() => ExecuteCommandCoreAsync(null, CancellationToken.None);

    private async Task ExecuteCommandCoreAsync(
        IRemoteExecutionSession? preparedSession,
        CancellationToken operationCt)
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
        _logService.IsExecuting = true;

        CancelAndDisposeCts();
        _executeCts = CancellationTokenSource.CreateLinkedTokenSource(operationCt);
        var ct = _executeCts.Token;

        var user = cred.UserName;
        var pwd = password ?? string.Empty;
        // A command that already names a shell must never be wrapped in another
        // shell (cmd /c cmd.exe, powershell -Command cmd.exe, ...): launch it
        // directly instead of building a nested helper window on the desktop.
        var isShellEntry = PsExecService.IsDirectShellEntry(command);
        var effectiveShell = isShellEntry ? CommandShell.Direct : SelectedShell;
        var wrapCmd = !isShellEntry;
        // Interactive launches must not inherit the UI's PowerShell default
        // merely because the selected shell is PowerShell. The service applies
        // the same policy again as a defense in depth, but resolve it here so
        // local launches and the remote session receive an identical shape.
        var (interactiveShell, interactiveWrapCmd) =
            PsExecService.ResolveInteractiveLaunchShape(command, SelectedShell, true);

        try
        {
            if (IsInteractiveMode)
            {
                var sessionId = ParseSessionId(SelectedSession);
                if (sessionId <= 0)
                {
                    _logService.Warn("无法解析所选会话 ID，已取消交互执行；请刷新会话列表后重试。");
                    await RefreshSessionsAsync();
                    return;
                }

                _logService.Info($"交互执行到会话 {sessionId}: {command}");
                ct.ThrowIfCancellationRequested();

                if (HostHelper.IsLocalHost(host))
                {
                    await _psExecService.ExecuteInteractiveLocalAsync(command, user, pwd, ct, interactiveShell, sessionId);
                    return;
                }

                // One fresh capability probe for this top-level interactive launch;
                // the plan is fixed for the whole operation and only a transport
                // failure may switch channels.
                var executionSession = preparedSession ??
                    await _execution.CreateSessionAsync(host, user, pwd, ct);
                var result = await executionSession.ExecuteAsync(
                    RemoteOperationKind.InteractiveLaunch,
                    new RemoteCommand
                    {
                        TargetHost = host,
                        Username = user,
                        Password = pwd,
                        Command = command,
                        Shell = interactiveShell,
                        InteractiveSession = true,
                        SessionId = sessionId,
                        WrapCmd = interactiveWrapCmd,
                    },
                    ct: ct);
                if (!result.Success)
                    _logService.Error($"交互执行失败: {result.StdErr}".Trim());
            }
            else
            {
                _logService.Info($"远程执行: {command}");

                if (HostHelper.IsLocalHost(host))
                {
                    await _psExecService.ExecuteWithOutputAsync(host, user, pwd,
                        command, line => _logService.Info(line), ct, shell: effectiveShell);
                    return;
                }

                // Control Panel / MMC entry points are GUI programs and must run on
                // the active desktop session instead of the redirected-output path.
                var isManagementEntry =
                    PsExecService.TryBuildManagementCommand(ProcessHelper.SplitCommandLine(command)) is not null;
                var operation = isManagementEntry
                    ? RemoteOperationKind.InteractiveLaunch
                    : RemoteOperationKind.Command;

                var executionSession = preparedSession ??
                    await _execution.CreateSessionAsync(host, user, pwd, ct);
                var result = await executionSession.ExecuteAsync(
                    operation,
                    new RemoteCommand
                    {
                        TargetHost = host,
                        Username = user,
                        Password = pwd,
                        Command = command,
                        Shell = isManagementEntry ? CommandShell.Direct : effectiveShell,
                        WrapCmd = isManagementEntry ? false : wrapCmd,
                    },
                    line => _logService.Info(line),
                    ct);
                if (!result.Success)
                    _logService.Error($"远程执行失败: {result.StdErr}".Trim());
            }
        }
        catch (OperationCanceledException)
        {
            _logService.Warn("执行已被用户中断。");
        }
        finally
        {
            _logService.IsExecuting = false;
            CancelAndDisposeCts();
            if (System.Windows.Application.Current is not null)
            {
                System.Windows.Input.Keyboard.ClearFocus();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_executeCts is { IsCancellationRequested: false })
        {
            _executeCts.Cancel();
            _logService.Warn("正在中断当前执行...");
        }
    }

    private void CancelAndDisposeCts()
    {
        if (_executeCts != null)
        {
            _executeCts.Cancel();
            _executeCts.Dispose();
            _executeCts = null;
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

    public async Task LoadScriptFileAsync(
        string localPath,
        bool executeImmediately = false,
        CancellationToken ct = default)
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
            var prepared = await PrepareScriptOnTargetAsync(
                host, localPath, fileName, executeImmediately, ct);
            var remoteScriptPath = prepared.RemotePath;

            // Build an explicit shell entry. This keeps drag-and-drop execution
            // deterministic and prevents the terminal's default PowerShell shell
            // from wrapping a .bat/.cmd path a second time.
            if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                CommandText = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{remoteScriptPath}\"";
            else
                CommandText = $"cmd.exe /d /s /c \"{remoteScriptPath}\"";

            _logService.Info(executeImmediately
                ? "脚本已上传，正在发送到目标主机执行。"
                : "执行命令已生成，点击「执行」或按 Enter 发送到目标主机。");

            if (executeImmediately)
                await ExecuteCommandCoreAsync(prepared.Session, ct);
        }
        catch (Exception ex)
        {
            _logService.Error($"脚本上传失败: {ex.Message}");
        }
    }

    private async Task<PreparedScript> PrepareScriptOnTargetAsync(
        string host,
        string localPath,
        string fileName,
        bool executeImmediately,
        CancellationToken ct)
    {
        if (HostHelper.IsLocalHost(host))
        {
            _logService.Info($"已加载本地脚本: {localPath}");
            return new PreparedScript(localPath, null);
        }

        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null)
            throw new InvalidOperationException("请先选择凭据。");

        var password = _main.Connection.CredentialService.DecryptPassword(cred) ?? string.Empty;
        // Drag-and-drop with immediate execution is one top-level operation: the
        // same capability snapshot must cover both the upload fallback and the
        // subsequent script launch.
        var session = executeImmediately
            ? await _execution.CreateSessionAsync(host, cred.UserName, password, ct)
            : null;
        var shareUpload = await _scriptShareUploader(host, cred.UserName, password, localPath, fileName);
        if (!string.IsNullOrWhiteSpace(shareUpload))
            return new PreparedScript(shareUpload, session);

        session ??= await _execution.CreateSessionAsync(host, cred.UserName, password, ct);
        var remotePath = await UploadScriptViaCommandAsync(
            session, host, cred.UserName, password, localPath, fileName, ct);
        return new PreparedScript(remotePath, executeImmediately ? session : null);
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

        _logService.Warn($"SMB 脚本上传失败，改用统一远程命令通道写入脚本: {string.Join("；", errors)}");
        return null;
    }

    private async Task<string> UploadScriptViaCommandAsync(
        IRemoteExecutionSession session,
        string host,
        string username,
        string password,
        string localPath,
        string fileName,
        CancellationToken ct)
    {
        var remoteScriptPath = $@"C:\Temp\{fileName}";
        var remoteBase64Path = $@"C:\Temp\{fileName}.b64";
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(localPath));

        // One top-level operation means one fresh capability probe. Every
        // initialization/chunk/finalization command must use that same fixed
        // snapshot; a transport failure may switch channels, but a remote command
        // failure must never be retried through another channel.
        _logService.Info("正在通过远程命令通道写入脚本到目标主机...");

        await RunUploadStepAsync(session, host, username, password,
            $"New-Item -ItemType Directory -Path 'C:\\Temp' -Force | Out-Null; " +
            $"Set-Content -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Value '' -NoNewline -Encoding ascii", ct);

        for (var offset = 0; offset < base64.Length; offset += UploadChunkSize)
        {
            var chunk = base64.Substring(offset, Math.Min(UploadChunkSize, base64.Length - offset));
            await RunUploadStepAsync(session, host, username, password,
                $"Add-Content -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Value {PowerShellLiteral(chunk)} -NoNewline -Encoding ascii", ct);
        }

        await RunUploadStepAsync(session, host, username, password,
            $"$b = Get-Content -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Raw -Encoding ascii; " +
            $"[IO.File]::WriteAllBytes({PowerShellLiteral(remoteScriptPath)}, [Convert]::FromBase64String($b)); " +
            $"Remove-Item -LiteralPath {PowerShellLiteral(remoteBase64Path)} -Force -ErrorAction SilentlyContinue", ct);

        _logService.Info($"脚本已通过远程命令通道写入目标主机: {remoteScriptPath}");
        return remoteScriptPath;
    }

    private static async Task RunUploadStepAsync(
        IRemoteExecutionSession session,
        string host,
        string username,
        string password,
        string script,
        CancellationToken ct)
    {
        var command = EncodePowerShellCommand(script);
        var result = await session.ExecuteAsync(
            RemoteOperationKind.Command,
            new RemoteCommand
            {
                TargetHost = host,
                Username = username,
                Password = password,
                Command = command,
                Shell = CommandShell.Direct,
                WrapCmd = false,
                Silent = true,
            },
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException($"远程写入脚本失败: {result.StdErr}".Trim());
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
    private sealed record PreparedScript(string RemotePath, IRemoteExecutionSession? Session);
}
