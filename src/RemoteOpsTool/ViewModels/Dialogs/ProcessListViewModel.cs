using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class ProcessListViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly INetworkService _networkService;
    private readonly ILogService _logService;
    private const int LoadTimeoutSec = 25;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedUserFilter = "全部用户";
    [ObservableProperty] private bool _isSigningOut;
    [ObservableProperty] private bool _hasUsers;
    [ObservableProperty] private bool _isAutoRefreshEnabled;
    [ObservableProperty] private int _refreshIntervalSeconds = 5;
    [ObservableProperty] private string _lastRefreshText = "尚未刷新";
    public ProcessRow? RightClickedRow { get; set; }
    public UserSessionRow? RightClickedSessionRow { get; set; }

    private List<ProcessRow> _processes = [];
    private List<UserSessionRow> _sessions = [];
    private int _loadVersion;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly DispatcherTimer _refreshTimer;

    public ObservableCollection<int> RefreshIntervals { get; } = [3, 5, 10, 30];

    public IEnumerable<ProcessRow> FilteredProcesses
    {
        get
        {
            var query = _processes.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(SearchText))
                query = query.Where(r =>
                    (r.ProcessName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    r.ProcessId.ToString().Contains(SearchText) ||
                    (r.UserName ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    (r.WindowTitle ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(SelectedUserFilter) && SelectedUserFilter != "全部用户")
                query = query.Where(r =>
                    (r.UserName ?? "").Equals(SelectedUserFilter, StringComparison.OrdinalIgnoreCase));
            return query;
        }
    }

    public ObservableCollection<UserSessionRow> UserSessions { get; } = [];
    public ObservableCollection<string> UserFilters { get; } = ["全部用户"];

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredProcesses));
    partial void OnSelectedUserFilterChanged(string value) => OnPropertyChanged(nameof(FilteredProcesses));
    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        if (value)
        {
            ApplyRefreshInterval();
            _refreshTimer.Start();
            _logService.Info($"进程管理已启用实时刷新，间隔 {RefreshIntervalSeconds} 秒。");
        }
        else
        {
            _refreshTimer.Stop();
            _logService.Info("进程管理实时刷新已关闭。");
        }
    }

    partial void OnRefreshIntervalSecondsChanged(int value)
    {
        if (!RefreshIntervals.Contains(value))
            RefreshIntervalSeconds = 5;
        ApplyRefreshInterval();
    }

    public ProcessListViewModel(MainViewModel main, INetworkService networkService, ILogService logService)
    {
        _main = main;
        _networkService = networkService;
        _logService = logService;
        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += async (_, _) => await LoadAsync(force: false, isAutoRefresh: true);
        ApplyRefreshInterval();
        _ = LoadAsync(force: true);
    }

    [RelayCommand]
    private async Task Refresh() => await LoadAsync(force: true);

    private void ApplyRefreshInterval()
        => _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(RefreshIntervalSeconds, 3, 60));

    private async Task LoadAsync(bool force, bool isAutoRefresh = false)
    {
        if (force)
            await _loadLock.WaitAsync();
        else if (!_loadLock.Wait(0))
            return;

        var version = Interlocked.Increment(ref _loadVersion);
        IsLoading = true;

        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);
            var isLocal = HostHelper.IsLocalHost(host);

            if (!isAutoRefresh)
                _logService.Info($"正在刷新进程管理数据: {host}");

            await LoadProcessesWithTimeoutAsync(host, cred.UserName, password ?? string.Empty, isLocal);
            if (version != _loadVersion) return;

            await LoadSessionsWithTimeoutAsync(host, cred.UserName, password ?? string.Empty);
            if (version != _loadVersion) return;

            EnrichProcessesFromCurrentSessions();
            UpdateUserFilters();
            LastRefreshText = $"最后刷新 {DateTime.Now:HH:mm:ss}";
            OnPropertyChanged(nameof(FilteredProcesses));
        }
        finally
        {
            IsLoading = false;
            _loadLock.Release();
        }
    }

    private async Task LoadProcessesWithTimeoutAsync(string host, string username, string password, bool isLocal)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(LoadTimeoutSec));
            List<ProcessDetailInfo> procs;
            if (isLocal)
            {
                procs = await Task.Run(() => BuildLocalProcessList(), cts.Token);

                if (procs.Count > 0)
                {
                    // Enrich: session names (session ID → "Console"/"Services" etc)
                    try
                    {
                        var nameMap = await Task.Run(() => NativeProcessHelper.BuildSessionNameMap(), cts.Token);
                        foreach (var p in procs)
                        {
                            if (int.TryParse(p.SessionName, out var sid) && nameMap.TryGetValue(sid, out var sname))
                                p.SessionName = sname;
                        }
                    }
                    catch { }

                    try
                    {
                        var titleMap = await Task.Run(() => NativeProcessHelper.BuildWindowTitleMap(), cts.Token);
                        foreach (var p in procs)
                        {
                            if (titleMap.TryGetValue(p.ProcessId, out var t) && !string.IsNullOrEmpty(t))
                                p.WindowTitle = t;
                        }
                    }
                    catch { }
                }
            }
            else
            {
                try
                {
                    procs = await _networkService.GetProcessListAsync(host, username, password, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    procs = [new() { ProcessName = "获取进程列表超时", ProcessId = -1 }];
                }
                catch (Exception ex)
                {
                    procs = [new() { ProcessName = $"错误: {ex.Message}", ProcessId = -1 }];
                }
            }

            if (cts.Token.IsCancellationRequested)
                procs = [new() { ProcessName = "获取进程列表超时", ProcessId = -1 }];

            _processes = procs.Where(p => p.ProcessId >= 0 || IsInfoRow(p))
                .Select(p => new ProcessRow(p)).ToList();
            if (_processes.Count == 0)
                _processes = [new(new ProcessDetailInfo { ProcessName = $"未获取到数据 (host={host})", ProcessId = 0 })];
        }
        catch (Exception ex)
        {
            _processes = [new(new ProcessDetailInfo { ProcessName = $"异常: {ex.Message}", ProcessId = -2 })];
        }
        OnPropertyChanged(nameof(FilteredProcesses));
    }

    private void EnrichProcessesFromCurrentSessions()
    {
        var processInfos = _processes.Select(p => p.Info).ToList();
        var sessionInfos = _sessions.Select(s => s.Info).ToList();
        EnrichUsernamesFromSessions(processInfos, sessionInfos);

        foreach (var session in sessionInfos)
        {
            foreach (var p in _processes)
            {
                if (p.Info.SessionId == session.SessionId ||
                    (p.Info.SessionId < 0 && int.TryParse(p.Info.SessionName, out var sid) && sid == session.SessionId))
                {
                    if (!string.IsNullOrWhiteSpace(session.Username) && string.IsNullOrWhiteSpace(p.Info.UserName))
                        p.Info.UserName = session.Username;
                    if (!string.IsNullOrWhiteSpace(session.SessionName))
                        p.Info.SessionName = session.SessionName;
                }
            }
        }

        if (_sessions.Count == 0)
        {
            var inferred = _processes
                .Where(p => !string.IsNullOrWhiteSpace(p.UserName) && (p.Info.SessionId >= 0 || !string.IsNullOrWhiteSpace(p.SessionName)))
                .GroupBy(p => $"{p.UserName}|{p.Info.SessionId}|{p.SessionName}", StringComparer.OrdinalIgnoreCase)
                .Select(g => new UserSessionRow(new UserSessionInfo
                {
                    Username = g.First().UserName,
                    SessionId = g.First().Info.SessionId,
                    SessionName = g.First().SessionName,
                    State = "由进程列表推断",
                    IdleTime = "",
                    LogonTime = ""
                }))
                .ToList();

            _sessions = inferred;
            UserSessions.Clear();
            foreach (var session in _sessions)
                UserSessions.Add(session);
            HasUsers = UserSessions.Count > 0;
        }

        foreach (var process in _processes)
            process.RefreshDisplay();
    }

    private static void EnrichUsernamesFromSessions(List<ProcessDetailInfo> procs, List<UserSessionInfo> sessions)
    {
        var sessionUserMap = new Dictionary<int, string>();
        foreach (var s in sessions)
        {
            if (!string.IsNullOrEmpty(s.Username) && !sessionUserMap.ContainsKey(s.SessionId))
                sessionUserMap[s.SessionId] = s.Username;
        }
        foreach (var p in procs)
        {
            var processSessionId = p.SessionId;
            if (processSessionId < 0 && int.TryParse(p.SessionName, out var sid))
                processSessionId = sid;

            if (string.IsNullOrEmpty(p.UserName) && processSessionId >= 0
                && sessionUserMap.TryGetValue(processSessionId, out var user))
                p.UserName = user;
        }
    }

    private static bool IsInfoRow(ProcessDetailInfo p) =>
        p.ProcessName.Contains("错误") || p.ProcessName.Contains("超时") || p.ProcessName.Contains("未获取");

    private static List<ProcessDetailInfo> BuildLocalProcessList()
    {
        try
        {
            var procs = Process.GetProcesses();
            var results = new List<ProcessDetailInfo>(procs.Length);
            foreach (var p in procs)
            {
                try
                {
                    var cpu = "";
                    try
                    {
                        var t = p.TotalProcessorTime;
                        if (t.Ticks > 0)
                            cpu = $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
                    }
                    catch { }

                    results.Add(new ProcessDetailInfo
                    {
                        ProcessName = p.ProcessName,
                        ProcessId = p.Id,
                        SessionId = p.SessionId,
                        SessionName = p.SessionId.ToString(),
                        MemoryMB = (p.WorkingSet64 / 1048576.0).ToString("F1"),
                        CpuTime = cpu,
                        Status = NativeProcessHelper.GetProcessStatus(p),
                        UserName = "",
                        WindowTitle = ""
                    });
                }
                catch
                {
                    try { results.Add(new ProcessDetailInfo { ProcessName = p.ProcessName, ProcessId = p.Id }); }
                    catch { }
                }
            }
            return results;
        }
        catch
        {
            return [new() { ProcessName = "进程枚举失败", ProcessId = -1 }];
        }
    }

    private static async Task<Dictionary<int, string>> GetSessionNameMapAsync()
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c query session",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains("sessionname", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Contains("services", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[^1], out var id))
                {
                    // session name is usually the second part
                    for (int i = 1; i < parts.Length; i++)
                    {
                        if (int.TryParse(parts[i], out _) && !map.ContainsKey(id))
                        {
                            map[id] = parts[i - 1];
                            break;
                        }
                    }
                }
            }
        }
        catch { }
        return map;
    }

    private async Task LoadSessionsWithTimeoutAsync(string host, string username, string password)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(LoadTimeoutSec));
            var sessions = await Task.Run(() => _networkService.GetUserSessionsAsync(host, username, password, cts.Token), cts.Token);

            _sessions = sessions.Select(s => new UserSessionRow(s)).ToList();

            UserSessions.Clear();
            foreach (var s in _sessions)
                UserSessions.Add(s);
            HasUsers = UserSessions.Count > 0;
        }
        catch
        {
            UserSessions.Clear();
            HasUsers = false;
        }
    }

    private void UpdateUserFilters()
    {
        var current = SelectedUserFilter;
        UserFilters.Clear();
        UserFilters.Add("全部用户");

        foreach (var username in _sessions.Select(s => s.Username)
                     .Concat(_processes.Select(p => p.UserName))
                     .Where(u => !string.IsNullOrWhiteSpace(u))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
        {
            UserFilters.Add(username);
        }

        SelectedUserFilter = UserFilters.Contains(current) ? current : "全部用户";
    }

    private List<ProcessRow> CheckedRows =>
        _processes.Where(r => r.IsChecked).ToList();

    [RelayCommand]
    private async Task KillProcessAsync() => await KillProcessesAsync(false);

    [RelayCommand]
    private async Task KillProcessTreeAsync() => await KillProcessesAsync(true);

    private async Task KillProcessesAsync(bool killTree)
    {
        var items = RightClickedRow != null
            ? [RightClickedRow]
            : CheckedRows;

        if (items.Count == 0) return;
        RightClickedRow = null;

        var host = _main.GetTargetHost();
        var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
        if (cred == null) return;
        var password = _main.Connection.CredentialService.DecryptPassword(cred);

        var killedPids = new HashSet<int>();
        foreach (var item in items)
        {
            if (item.Info.ProcessId <= 0 || killedPids.Contains(item.Info.ProcessId)) continue;
            killedPids.Add(item.Info.ProcessId);
            await _networkService.KillProcessAsync(host, cred.UserName, password ?? string.Empty,
                item.Info.ProcessId, killTree);
        }
        await LoadAsync(force: true);
    }

    [RelayCommand]
    private async Task SignOutUserAsync()
    {
        var session = RightClickedSessionRow;
        RightClickedSessionRow = null;
        if (session == null || session.SessionId <= 0) return;

        IsSigningOut = true;
        try
        {
            var host = _main.GetTargetHost();
            var cred = _main.Connection.CredentialService.GetSelectedCredentials().FirstOrDefault();
            if (cred == null) return;
            var password = _main.Connection.CredentialService.DecryptPassword(cred);

            var success = await _networkService.SignOutUserAsync(host, cred.UserName, password ?? string.Empty,
                session.SessionId);
            if (success)
                _logService.Info($"已注销用户 {session.Username} (会话ID: {session.SessionId})");
            await LoadAsync(force: true);
        }
        finally
        {
            IsSigningOut = false;
        }
    }

    [RelayCommand]
    private async Task SignOutSelectedUserAsync()
    {
        var checkedSession = _sessions.FirstOrDefault(s => s.IsChecked);
        if (checkedSession == null) return;

        RightClickedSessionRow = checkedSession;
        await SignOutUserAsync();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _loadLock.Dispose();
    }
}

public partial class ProcessRow : ObservableObject
{
    public ProcessDetailInfo Info { get; }
    [ObservableProperty] private bool _isChecked;

    public string ProcessName => Info.ProcessName;
    public int ProcessId => Info.ProcessId;
    public string SessionName => Info.SessionName;
    public string MemoryMB => Info.MemoryMB;
    public string CpuTime => Info.CpuTime;
    public string Status => Info.Status;
    public string UserName => Info.UserName;
    public string WindowTitle => Info.WindowTitle;

    public ProcessRow(ProcessDetailInfo info) { Info = info; }

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(SessionName));
        OnPropertyChanged(nameof(UserName));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(WindowTitle));
    }
}

public partial class UserSessionRow : ObservableObject
{
    public UserSessionInfo Info { get; }
    [ObservableProperty] private bool _isChecked;

    public string Username => Info.Username;
    public string SessionName => Info.SessionName;
    public int SessionId => Info.SessionId;
    public string State => Info.State;
    public string IdleTime => Info.IdleTime;
    public string LogonTime => Info.LogonTime;

    public UserSessionRow(UserSessionInfo info) { Info = info; }
}
