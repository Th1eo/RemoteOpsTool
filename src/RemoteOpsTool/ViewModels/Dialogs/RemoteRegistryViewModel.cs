using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class RemoteRegistryViewModel : ObservableObject
{
    private readonly string _host, _username, _password;
    private readonly IPsExecService _psExec;
    private readonly ILogService _log;
    private readonly bool _isLocal;
    private CancellationTokenSource? _loadCts;
    private readonly HashSet<RegistryTreeNode> _loadingNodes = [];
    private const int LoadTimeoutSec = 60;
    private string _resolvedHkcuSid = "";
    private string _resolvedHkcuUser = "";
    private readonly List<(string sid, string username)> _loggedOnUsers = [];

    [ObservableProperty] private string _currentPath = "HKLM";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private RegValueDisplay? _selectedValue;
    [ObservableProperty] private string _selectedUser = "";
    [ObservableProperty] private List<string> _availableUsers = [];
    public bool IsDefaultValueSelected => SelectedValue != null && SelectedValue.Name == "(默认)";

    public ObservableCollection<RegistryTreeNode> RootNodes { get; } = [];
    public ObservableCollection<RegValueDisplay> Values { get; } = [];

    public RemoteRegistryViewModel(string host, string username, string password, IPsExecService psExec, ILogService log)
    {
        _host = host; _username = username; _password = password; _psExec = psExec; _log = log;
        _isLocal = host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || host is "localhost" or "127.0.0.1" or "::1" or ".";
    }

    public async Task InitializeAsync()
    {
        if (!_isLocal)
            await ResolveLoggedOnUsersAsync();
        LoadHives();
    }

    private string RegPath(string hiveOrPath)
    {
        if (_isLocal) return hiveOrPath;

        if (hiveOrPath.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = hiveOrPath.Length > 4 ? hiveOrPath[4..] : "";
            if (!string.IsNullOrEmpty(_resolvedHkcuSid))
                return $"HKU\\{_resolvedHkcuSid}{remainder}";
            return $"HKU\\S-1-5-18{remainder}";
        }

        if (hiveOrPath.StartsWith("HKCR", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = hiveOrPath.Length > 4 ? hiveOrPath[4..] : "";
            return $"\\\\{_host}\\HKLM\\SOFTWARE\\Classes{remainder}";
        }

        return $"\\\\{_host}\\{hiveOrPath}";
    }

    private void CancelLoad()
    {
        if (_loadCts != null)
        {
            try { _loadCts.Cancel(); _loadCts.Dispose(); } catch { }
            _loadCts = null;
        }
    }

    private async Task ResolveLoggedOnUsersAsync()
    {
        try
        {
            _loggedOnUsers.Clear();

            var sessionResult = await _psExec.ExecuteAsync(_host, _username, _password,
                "query user", silent: true);
            if (sessionResult.Success && !string.IsNullOrWhiteSpace(sessionResult.StdOut))
            {
                ParseQueryUserOutput(sessionResult.StdOut);
            }

            if (_loggedOnUsers.Count == 0)
            {
                await ResolveUsersFromRegistry();
            }

            AvailableUsers = _loggedOnUsers.Select(u => u.username).ToList();

            var defaultUser = _loggedOnUsers.FirstOrDefault(
                u => u.username.IndexOf("console", StringComparison.OrdinalIgnoreCase) >= 0
                  || u.username.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0)
                .username
                ?? _loggedOnUsers.FirstOrDefault().username
                ?? "";

            if (!string.IsNullOrEmpty(defaultUser))
            {
                SelectedUser = defaultUser;
                await SwitchToUserAsync(defaultUser);
            }
        }
        catch { }
    }

    private void ParseQueryUserOutput(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var headerSkipped = false;
        foreach (var line in lines)
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
            if (line.StartsWith(" USERNAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Trim().StartsWith(">")) continue;

            var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var username = parts[0].Trim('>', ' ');
            var sessionName = parts.Length > 1 ? parts[1] : "";
            var sessionId = parts.Length > 2 ? parts[2] : "";
            var state = parts.Length > 3 ? parts[3] : "";

            if (username.Contains('\\'))
                continue;

            if (username.StartsWith("NT ", StringComparison.OrdinalIgnoreCase)) continue;

            var displayName = $"{username} (会话{sessionId} {state})";
        }
    }

    private async Task ResolveUsersFromRegistry()
    {
        try
        {
            var hkuResult = await _psExec.ExecuteAsync(_host, _username, _password,
                "reg query \"HKU\"", silent: true);
            if (!hkuResult.Success) return;

            var lines = hkuResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var candidateSids = new List<(string sid, string line)>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("HKEY_USERS\\S-1-", StringComparison.OrdinalIgnoreCase)) continue;
                var sid = trimmed["HKEY_USERS\\".Length..].Trim();
                if (sid.Length < 30) continue;
                candidateSids.Add((sid, trimmed));
            }

            var checkTasks = candidateSids.Select(async item =>
            {
                var (sid, _) = item;
                var checkCmd = $"reg query \"HKU\\{sid}\\Volatile Environment\" /v USERNAME 2>nul";
                var check = await _psExec.ExecuteAsync(_host, _username, _password, checkCmd, silent: true);
                return (sid, check);
            });

            var results = await Task.WhenAll(checkTasks);
            foreach (var (sid, check) in results)
            {
                if (!check.Success) continue;
                foreach (var cl in check.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = cl.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                    var lastIdx = parts.Length > 0 ? parts[^1] : "";
                    if (lastIdx.Length > 0 && lastIdx.Length < 50
                        && !lastIdx.StartsWith("REG_", StringComparison.OrdinalIgnoreCase)
                        && !lastIdx.StartsWith("USERNAME", StringComparison.OrdinalIgnoreCase))
                    {
                        var displayName = $"{lastIdx} (HKU)";
                        if (!_loggedOnUsers.Any(u => u.sid == sid))
                            _loggedOnUsers.Add((sid, displayName));
                        break;
                    }
                }
            }
        }
        catch { }
    }

    private async Task SwitchToUserAsync(string userDisplay)
    {
        var match = _loggedOnUsers.FirstOrDefault(u => u.username == userDisplay);
        if (!string.IsNullOrEmpty(match.sid))
        {
            _resolvedHkcuSid = match.sid;
            _resolvedHkcuUser = match.username;
            _log.Info($"已切换注册表用户: {match.username} (SID: {_resolvedHkcuSid})");
        }
        else
        {
            var sid = await ResolveUserSidAsync(userDisplay);
            if (!string.IsNullOrEmpty(sid))
            {
                _resolvedHkcuSid = sid;
                _resolvedHkcuUser = userDisplay;
                _log.Info($"已解析用户 SID: {sid}");
            }
        }
    }

    private async Task<string> ResolveUserSidAsync(string userDisplay)
    {
        try
        {
            var psCmd = "powershell -NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'Name=''explorer.exe''' | Select-Object -First 1).GetOwnerSid().Sid\"";
            var result = await _psExec.ExecuteAsync(_host, _username, _password, psCmd, silent: true);
            if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                var sid = result.StdOut.Trim();
                if (sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) && sid.Length > 20)
                    return sid;
            }
        }
        catch { }

        try
        {
            var hkuResult = await _psExec.ExecuteAsync(_host, _username, _password,
                "reg query \"HKU\"", silent: true);
            if (!hkuResult.Success) return "";

            var name = userDisplay.Contains('(') ? userDisplay[..userDisplay.IndexOf('(')].Trim() : userDisplay.Trim();
            var lines = hkuResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var candidateSids = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("HKEY_USERS\\S-1-", StringComparison.OrdinalIgnoreCase)) continue;
                var sid = trimmed["HKEY_USERS\\".Length..].Trim();
                if (sid.Length >= 30)
                    candidateSids.Add(sid);
            }

            var checkTasks = candidateSids.Select(async sid =>
            {
                var checkCmd = $"reg query \"HKU\\{sid}\\Volatile Environment\" /v USERNAME 2>nul";
                var check = await _psExec.ExecuteAsync(_host, _username, _password, checkCmd, silent: true);
                return (sid, check.Success && check.StdOut.Contains(name, StringComparison.OrdinalIgnoreCase));
            });

            var results = await Task.WhenAll(checkTasks);
            foreach (var (sid, matched) in results)
            {
                if (matched)
                    return sid;
            }
        }
        catch { }

        return "";
    }

    public void LoadHives()
    {
        RootNodes.Clear();
        foreach (var hive in new[] { "HKLM", "HKCU", "HKU", "HKCR" })
        {
            RootNodes.Add(new RegistryTreeNode
            {
                Name = hive,
                FullPath = hive,
                Children = new[] { RegistryTreeNode.Placeholder }
            });
        }
    }

    public async void LoadChildrenAsync(RegistryTreeNode node)
    {
        var children = node.Children;
        if (children != null && children.Count > 0
            && children[0] != RegistryTreeNode.Placeholder
            && children[0] != RegistryTreeNode.TimeoutNode
            && children[0] != RegistryTreeNode.ErrorNode)
            return;

        lock (_loadingNodes)
        {
            if (!_loadingNodes.Add(node)) return;
        }

        CancelLoad();
        _loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(LoadTimeoutSec));
        var ct = _loadCts.Token;

        try
        {
            IsLoading = true;
            StatusText = $"正在加载 \"{node.Name}\"...";

            IReadOnlyList<RegistryTreeNode> result;

            if (_isLocal)
            {
                var fullPaths = await Task.Run(() => RegistryHelper.GetSubKeyFullPaths(node.FullPath), ct);
                result = fullPaths.Select(fp =>
                {
                    var name = fp[(fp.LastIndexOf('\\') + 1)..];
                    return new RegistryTreeNode
                    {
                        Name = name,
                        FullPath = fp,
                        Children = new[] { RegistryTreeNode.Placeholder }
                    };
                }).ToArray();
            }
            else
            {
                result = await LoadRemoteChildrenStreamingAsync(node, ct);
            }

            node.Children = result;
            StatusText = result.Count > 0 ? $"已加载 {result.Count} 个子项" : "无子项";
        }
        catch (OperationCanceledException)
        {
            StatusText = "加载超时或被取消";
            if (node.Children == null || (node.Children.Count == 1 && node.Children[0] == RegistryTreeNode.Placeholder))
                node.Children = new[] { RegistryTreeNode.TimeoutNode };
        }
        catch (Exception ex)
        {
            StatusText = "加载失败";
            _log.Error($"加载注册表子项失败: {ex.Message}");
            if (node.Children == null || (node.Children.Count == 1 && node.Children[0] == RegistryTreeNode.Placeholder))
                node.Children = new[] { RegistryTreeNode.ErrorNode };
        }
        finally
        {
            IsLoading = false;
            lock (_loadingNodes) { _loadingNodes.Remove(node); }
        }
    }

    private async Task<IReadOnlyList<RegistryTreeNode>> LoadRemoteChildrenStreamingAsync(RegistryTreeNode node, CancellationToken ct)
    {
        var cmd = $"reg query \"{RegPath(node.FullPath)}\"";
        var list = new List<RegistryTreeNode>();

        await _psExec.ExecuteWithOutputAsync(_host, _username, _password, cmd, line =>
        {
            if (ct.IsCancellationRequested) return;
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return;
            if (!trimmed.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)) return;

            var subPath = trimmed;
            var name = subPath[(subPath.LastIndexOf('\\') + 1)..];

            lock (list)
            {
                list.Add(new RegistryTreeNode
                {
                    Name = name,
                    FullPath = subPath,
                    Children = new[] { RegistryTreeNode.Placeholder }
                });
            }

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background, () =>
                {
                    StatusText = $"正在加载 \"{node.Name}\"... ({list.Count} 项)";
                });
        }, ct);

        return list;
    }

    public void RefreshTreeNode(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var node = FindNodeByPath(path);
        if (node != null)
        {
            node.Children = new[] { RegistryTreeNode.Placeholder };
            LoadChildrenAsync(node);
        }
    }

    private RegistryTreeNode? FindNodeByPath(string path)
    {
        foreach (var root in RootNodes)
        {
            if (root.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                return root;
            var found = FindInTree(root, path);
            if (found != null) return found;
        }
        return null;
    }

    private static RegistryTreeNode? FindInTree(RegistryTreeNode parent, string path)
    {
        var children = parent.Children;
        if (children == null) return null;
        foreach (var child in children)
        {
            if (child.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                return child;
            var found = FindInTree(child, path);
            if (found != null) return found;
        }
        return null;
    }

    [RelayCommand]
    private async Task NavigateAsync()
    {
        CancelLoad();
        _loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(LoadTimeoutSec));
        var ct = _loadCts.Token;

        Values.Clear();
        var path = CurrentPath?.Trim();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            IsLoading = true;
            StatusText = "正在加载值...";

            if (_isLocal)
            {
                var infos = await Task.Run(() => RegistryHelper.GetValues(path), ct);
                foreach (var info in infos)
                    Values.Add(info);
            }
            else
            {
                var cmd = $"reg query \"{RegPath(path)}\"";
                var result = await _psExec.ExecuteAsync(_host, _username, _password, cmd, ct: ct);
                if (!result.Success)
                {
                    StatusText = "查询失败";
                    return;
                }
                foreach (var line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim();
                    if (string.IsNullOrWhiteSpace(t) || !t.StartsWith("    ")) continue;
                    var idx1 = t.IndexOf("    ", StringComparison.Ordinal);
                    if (idx1 < 0) continue;
                    var name = t[..idx1].Trim();
                    var rest = t[(idx1 + 4)..].Trim();
                    var idx2 = rest.IndexOf("    ", StringComparison.Ordinal);
                    var type = idx2 > 0 ? rest[..idx2].Trim() : rest;
                    var val = idx2 > 0 ? rest[(idx2 + 4)..].Trim() : "";
                    if (!string.IsNullOrEmpty(name))
                        Values.Add(new RegValueDisplay { Name = name, Type = type, Value = val });
                }
            }
            StatusText = Values.Count > 0 ? $"已加载 {Values.Count} 个值" : "无值";
        }
        catch (OperationCanceledException)
        {
            StatusText = "加载超时或被取消";
        }
        catch (Exception ex)
        {
            StatusText = "加载失败";
            _log.Error($"加载注册表值失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ModifyValueAsync()
    {
        if (SelectedValue == null) return;
        var sv = SelectedValue;

        var dlg = new ValueEditDialog(sv.Name, sv.Type, sv.Value);
        if (dlg.ShowDialog() != true) return;
        if (string.IsNullOrEmpty(dlg.ValueName)) return;

        var newName = dlg.ValueName;
        var newData = dlg.ValueData ?? "";

        if (!string.Equals(newName, sv.Name, StringComparison.OrdinalIgnoreCase))
        {
            var delCmd = $"reg delete \"{RegPath(CurrentPath)}\" /v \"{sv.Name}\" /f";
            await _psExec.ExecuteAsync(_host, _username, _password, delCmd);
        }

        var addCmd = $"reg add \"{RegPath(CurrentPath)}\" /v \"{newName}\" /t {sv.Type} /d \"{newData}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, addCmd);
        await NavigateAsync();
    }

    [RelayCommand]
    private async Task DeleteValueAsync()
    {
        if (SelectedValue == null) return;
        var sv = SelectedValue;
        var dlg = new ConfirmDialog("删除值", $"确定要删除值 \"{sv.Name}\" 吗？");
        if (dlg.ShowDialog() != true || !dlg.Confirmed) return;

        var cmd = $"reg delete \"{RegPath(CurrentPath)}\" /v \"{sv.Name}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, cmd);
        await NavigateAsync();
    }

    [RelayCommand]
    private async Task DeleteKeyAsync()
    {
        var dlg = new ConfirmDialog("删除项", $"确定要删除项 \"{CurrentPath}\" 吗？");
        if (dlg.ShowDialog() != true || !dlg.Confirmed) return;

        var path = CurrentPath?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        var parentPath = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";

        var cmd = $"reg delete \"{RegPath(path)}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, cmd);
        RefreshTreeNode(parentPath);
        CurrentPath = parentPath.Length > 0 ? parentPath : "HKLM";
        Values.Clear();
        StatusText = "已删除";
    }

    [RelayCommand]
    private async Task RenameKeyAsync()
    {
        var path = CurrentPath?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        var oldName = path[(path.LastIndexOf('\\') + 1)..];
        if (oldName.Length == 0) return;

        var dlg = new InputDialog("重命名项", $"输入新名称（当前: {oldName}）：");
        dlg.Owner = System.Windows.Application.Current.MainWindow;
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;

        var parentPath = path[..path.LastIndexOf('\\')];
        var copyCmd = $"reg copy \"{RegPath(path)}\" \"{RegPath(parentPath)}\\{dlg.Result}\" /s /f";
        var result = await _psExec.ExecuteAsync(_host, _username, _password, copyCmd);
        if (result.Success)
        {
            var delCmd = $"reg delete \"{RegPath(path)}\" /f";
            await _psExec.ExecuteAsync(_host, _username, _password, delCmd);
        }
        RefreshTreeNode(parentPath);
        CurrentPath = $"{parentPath}\\{dlg.Result}";
        StatusText = "已重命名";
        await NavigateAsync();
    }

    [RelayCommand]
    private async Task RenameValueAsync()
    {
        if (SelectedValue == null) return;
        var sv = SelectedValue;
        var dlg = new InputDialog("重命名值", $"输入新名称（当前: {sv.Name}）：");
        dlg.Owner = System.Windows.Application.Current.MainWindow;
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;

        var delCmd = $"reg delete \"{RegPath(CurrentPath)}\" /v \"{sv.Name}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, delCmd);
        var addCmd = $"reg add \"{RegPath(CurrentPath)}\" /v \"{dlg.Result}\" /t {sv.Type} /d \"{sv.Value}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, addCmd);
        await NavigateAsync();
    }

    [RelayCommand]
    private async Task NewKeyAsync()
    {
        var dlg = new NewKeyDialog();
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.KeyName)) return;
        var cmd = $"reg add \"{RegPath(CurrentPath)}\\{dlg.KeyName}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, cmd);
        RefreshTreeNode(CurrentPath);
        await NavigateAsync();
    }

    [RelayCommand]
    private async Task NewStringValueAsync()
    {
        var dlg = new ValueEditDialog("REG_SZ");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ValueName)) return;
        await SetRegValue(dlg.ValueName, "REG_SZ", dlg.ValueData ?? "");
    }

    [RelayCommand]
    private async Task NewBinaryValueAsync()
    {
        var dlg = new ValueEditDialog("REG_BINARY");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ValueName)) return;
        await SetRegValue(dlg.ValueName, "REG_BINARY", dlg.ValueData ?? "");
    }

    [RelayCommand]
    private async Task NewDwordValueAsync()
    {
        var dlg = new ValueEditDialog("REG_DWORD");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ValueName)) return;
        await SetRegValue(dlg.ValueName, "REG_DWORD", dlg.ValueData ?? "0");
    }

    [RelayCommand]
    private async Task NewQwordValueAsync()
    {
        var dlg = new ValueEditDialog("REG_QWORD");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ValueName)) return;
        await SetRegValue(dlg.ValueName, "REG_QWORD", dlg.ValueData ?? "0");
    }

    [RelayCommand]
    private async Task NewMultiStringValueAsync()
    {
        var dlg = new ValueEditDialog("REG_MULTI_SZ");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ValueName)) return;
        await SetRegValue(dlg.ValueName, "REG_MULTI_SZ", dlg.ValueData ?? "");
    }

    [RelayCommand]
    private async Task NewExpandStringValueAsync()
    {
        var dlg = new ValueEditDialog("REG_EXPAND_SZ");
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ValueName)) return;
        await SetRegValue(dlg.ValueName, "REG_EXPAND_SZ", dlg.ValueData ?? "");
    }

    private async Task SetRegValue(string name, string type, string data)
    {
        var cmd = $"reg add \"{RegPath(CurrentPath)}\" /v \"{name}\" /t {type} /d \"{data}\" /f";
        await _psExec.ExecuteAsync(_host, _username, _password, cmd);
        await NavigateAsync();
    }
}

public class RegValueDisplay
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}
