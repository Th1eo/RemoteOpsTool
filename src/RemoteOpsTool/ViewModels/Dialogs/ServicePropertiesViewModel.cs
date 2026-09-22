using System.Collections.ObjectModel;
using System.Management;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.Services.Capability;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;
using RemoteOpsTool.Views.Dialogs;

namespace RemoteOpsTool.ViewModels.Dialogs;

public partial class ServicePropertiesViewModel : ObservableObject
{
    private readonly string _host, _username, _password, _serviceName;
    private readonly IPsExecService _psExec;
    private readonly IRemoteExecutionService _execution;
    private readonly ILogService _log;
    private readonly bool _isLocal;
    private readonly ICacheService? _cache;
    private readonly string? _propertiesCacheKey;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _cacheWriteGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _wmiGate = new(1, 1);

    private IRemoteExecutionSession? _remoteSession;
    private ManagementScope? _wmiScope;

    private int _loadingCount;
    private bool _dependenciesLoading;
    private bool _dependenciesLoaded;
    private bool _recoveryLoading;
    private bool _recoveryLoaded;
    private ServicePropertiesCacheData? _cacheData;

    /// <summary>加载完成后的服务配置快照；应用修改时据此只提交真正变化的项。</summary>
    private ServiceConfigSnapshot? _originalSnapshot;

    /// <summary>由对话框在用户点击“应用/确定”时写入的密码输入（不参与绑定，避免明文外泄）。</summary>
    private PasswordData _credentials = PasswordData.Empty;

    private readonly record struct PasswordData(string Password, string Confirm)
    {
        public static PasswordData Empty => new(string.Empty, string.Empty);
    }

    public ServicePropertiesViewModel(string host, string username, string password,
        string serviceName, string displayName, IPsExecService psExec,
        IRemoteExecutionService execution, ILogService log,
        ServiceInfo? initialService = null, ICacheService? cache = null)
    {
        _host = host; _username = username; _password = password;
        _serviceName = serviceName; _displayName = displayName;
        _psExec = psExec; _execution = execution; _log = log;
        _cache = cache;
        _propertiesCacheKey = cache == null ? null : CacheKeys.ServiceProperties(_serviceName, _username);
        _isLocal = HostHelper.IsLocalHost(host);

        if (initialService != null)
            ApplyInitialService(initialService);

        _ = LoadAsync(_lifetimeCts.Token);
    }

    [ObservableProperty] private string _displayName = "";
    public string ServiceName => _serviceName;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _binaryPath = "";
    [ObservableProperty] private string _startParams = "";
    [ObservableProperty] private string _selectedStartType = "";
    [ObservableProperty] private string _serviceStatus = "";
    [ObservableProperty] private string _statusColor = "#6C6A64";
    [ObservableProperty] private bool _canStart, _canStop, _canPause, _canResume;
    public List<string> StartTypes { get; } = [.. ServiceCommandHelper.UiStartTypes];

    [ObservableProperty] private bool _useLocalSystem = true;
    [ObservableProperty] private bool _useThisAccount;
    [ObservableProperty] private string _logOnAccount = "";
    [ObservableProperty] private bool _allowDesktopInteract;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _loadingText = "正在读取服务基本信息…";
    [ObservableProperty] private int _selectedTabIndex;
    /// <summary>加载或应用过程中禁止重复提交：Apply/Ok/状态开关按钮都绑定它。</summary>
    public bool IsIdle => !IsLoading && !IsApplying;

    public List<string> FailureActions { get; } = [.. ServiceCommandHelper.UiFailureActions];
    [ObservableProperty] private string _firstFailure = "不操作";
    [ObservableProperty] private string _secondFailure = "不操作";
    [ObservableProperty] private string _subsequentFailure = "不操作";
    [ObservableProperty] private string _resetFailDays = "1";
    [ObservableProperty] private string _restartMinutes = "1";

    public ObservableCollection<string> Dependencies { get; } = [];
    public ObservableCollection<string> DependentServices { get; } = [];

    /// <summary>“应用/确定”成功后请求对话框关闭。</summary>
    public event Action? CloseRequested;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnIsApplyingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1)
            _ = EnsureRecoveryLoadedAsync(_lifetimeCts.Token);
        else if (value == 2)
            _ = EnsureDependenciesLoadedAsync(_lifetimeCts.Token);
    }

    /// <summary>窗口关闭时取消尚未完成的查询，避免后台任务继续更新已关闭的界面。</summary>
    public void CancelLoading()
    {
        _lifetimeCts.Cancel();
        _remoteSession = null;
        _wmiScope = null;
    }

    private void BeginLoading(string text)
    {
        _loadingCount++;
        LoadingText = text;
        IsLoading = true;
    }

    private void EndLoading()
    {
        if (_loadingCount > 0)
            _loadingCount--;

        if (_loadingCount == 0)
            IsLoading = false;
    }

    partial void OnUseThisAccountChanged(bool value)
    {
        if (value) UseLocalSystem = false;
    }

    partial void OnUseLocalSystemChanged(bool value)
    {
        if (value) UseThisAccount = false;
    }

    private async Task LoadAsync(CancellationToken ct = default)
    {
        BeginLoading("正在读取服务基本信息…");
        try
        {
            if (await TryPopulateFromCacheAsync(ct))
                LoadingText = "已显示缓存，正在刷新…";

            var loaded = !_isLocal && await TryLoadViaWmiAsync(ct);
            if (!loaded)
                loaded = await TryLoadCoreViaScAsync(ct);

            if (loaded)
            {
                ExtractStartParams();
                CaptureSnapshot();
                _cacheData = BuildCacheData();
                await SaveCacheAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户关闭窗口时取消未完成的查询。
        }
        catch (Exception ex)
        {
            _log.Debug($"服务属性加载失败: {_serviceName} - {ex.Message}");
        }
        finally
        {
            EndLoading();
        }
    }

    private void ApplyInitialService(ServiceInfo service)
    {
        if (!string.IsNullOrWhiteSpace(service.DisplayName))
            DisplayName = service.DisplayName;

        SelectedStartType = NormalizeStartType(service.StartType);
        ApplyStatus(service.Status);
    }

    private async Task<bool> TryPopulateFromCacheAsync(CancellationToken ct)
    {
        if (_cache == null || _propertiesCacheKey == null)
            return false;

        var cached = await _cache.GetAsync<ServicePropertiesCacheData>(_host, _propertiesCacheKey);
        ct.ThrowIfCancellationRequested();
        if (cached == null)
            return false;

        if (!string.IsNullOrWhiteSpace(cached.DisplayName)) DisplayName = cached.DisplayName;
        Description = cached.Description;
        BinaryPath = cached.BinaryPath;
        StartParams = cached.StartParams;
        if (!string.IsNullOrWhiteSpace(cached.SelectedStartType)) SelectedStartType = cached.SelectedStartType;
        if (!string.IsNullOrWhiteSpace(cached.ServiceStatus)) ServiceStatus = cached.ServiceStatus;
        StatusColor = cached.StatusColor;
        CanStart = cached.CanStart;
        CanStop = cached.CanStop;
        CanPause = cached.CanPause;
        CanResume = cached.CanResume;
        UseLocalSystem = cached.UseLocalSystem;
        UseThisAccount = cached.UseThisAccount;
        LogOnAccount = cached.LogOnAccount;
        AllowDesktopInteract = cached.AllowDesktopInteract;

        FirstFailure = cached.FirstFailure;
        SecondFailure = cached.SecondFailure;
        SubsequentFailure = cached.SubsequentFailure;
        ResetFailDays = cached.ResetFailDays;
        RestartMinutes = cached.RestartMinutes;

        Dependencies.Clear();
        DependentServices.Clear();
        foreach (var dependency in cached.Dependencies)
            Dependencies.Add(dependency);
        foreach (var dependent in cached.DependentServices)
            DependentServices.Add(dependent);

        // TTL 内直接复用快照；未缓存过的分区仍按用户实际打开的 Tab 懒加载。
        _dependenciesLoaded = cached.HasDependencyData;
        _recoveryLoaded = cached.HasRecoveryData;
        CaptureSnapshot();
        _cacheData = cached;
        return true;
    }

    private async Task EnsureRecoveryLoadedAsync(CancellationToken ct)
    {
        if (_recoveryLoaded || _recoveryLoading)
            return;

        _recoveryLoading = true;
        BeginLoading("正在读取失败恢复策略…");
        try
        {
            if (await LoadFailureActionsAsync(ct))
            {
                _recoveryLoaded = true;

                // 懒加载完成时只更新快照中的失败恢复字段，不能覆盖用户已编辑的常规配置。
                if (_originalSnapshot != null)
                {
                    _originalSnapshot = _originalSnapshot with
                    {
                        FirstFailure = FirstFailure,
                        SecondFailure = SecondFailure,
                        SubsequentFailure = SubsequentFailure,
                        ResetFailDays = ResetFailDays,
                        RestartMinutes = RestartMinutes,
                    };
                }

                if (_cacheData != null)
                {
                    _cacheData.FirstFailure = FirstFailure;
                    _cacheData.SecondFailure = SecondFailure;
                    _cacheData.SubsequentFailure = SubsequentFailure;
                    _cacheData.ResetFailDays = ResetFailDays;
                    _cacheData.RestartMinutes = RestartMinutes;
                    _cacheData.HasRecoveryData = true;
                    await SaveCacheAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭时取消。
        }
        catch (Exception ex)
        {
            _log.Debug($"失败恢复策略加载失败: {_serviceName} - {ex.Message}");
        }
        finally
        {
            _recoveryLoading = false;
            EndLoading();
        }
    }

    private async Task EnsureDependenciesLoadedAsync(CancellationToken ct)
    {
        if (_dependenciesLoaded || _dependenciesLoading)
            return;

        _dependenciesLoading = true;
        BeginLoading("正在读取依存关系…");
        try
        {
            var loaded = !_isLocal && await TryLoadDependenciesViaWmiAsync(ct);
            if (!loaded)
                loaded = await TryLoadDependenciesViaScAsync(ct);

            if (loaded)
            {
                _dependenciesLoaded = true;
                if (_cacheData != null)
                {
                    _cacheData.Dependencies = [.. Dependencies];
                    _cacheData.DependentServices = [.. DependentServices];
                    _cacheData.HasDependencyData = true;
                    await SaveCacheAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭时取消。
        }
        catch (Exception ex)
        {
            _log.Debug($"依存关系加载失败: {_serviceName} - {ex.Message}");
        }
        finally
        {
            _dependenciesLoading = false;
            EndLoading();
        }
    }

    private async Task<bool> TryLoadDependenciesViaScAsync(CancellationToken ct)
    {
        try
        {
            var qcResult = _isLocal
                ? await ProcessHelper.RunAsync("sc.exe", $"qc \"{_serviceName}\"", ct)
                : await ExecuteRemoteCommandAsync($"sc qc \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true, ct: ct);

            var enumResult = _isLocal
                ? await ProcessHelper.RunAsync("sc.exe", $"enumdepend \"{_serviceName}\"", ct)
                : await ExecuteRemoteCommandAsync($"sc enumdepend \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true, ct: ct);

            if (!qcResult.Success && !enumResult.Success)
                return false;

            var dependencies = qcResult.Success
                ? qcResult.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.TrimStart().StartsWith("DEPENDENCIES", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(ParseDependencyList)
                : [];

            var dependentServices = enumResult.Success
                ? ParseEnumDependOutput(enumResult.StdOut)
                : [];

            ReplaceDependencies(dependencies, dependentServices);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"sc 依存关系查询失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }

    private ServicePropertiesCacheData BuildCacheData() => new()
    {
        ServiceName = _serviceName,
        DisplayName = DisplayName,
        Description = Description,
        BinaryPath = BinaryPath,
        StartParams = StartParams,
        SelectedStartType = SelectedStartType,
        ServiceStatus = ServiceStatus,
        StatusColor = StatusColor,
        CanStart = CanStart,
        CanStop = CanStop,
        CanPause = CanPause,
        CanResume = CanResume,
        UseLocalSystem = UseLocalSystem,
        UseThisAccount = UseThisAccount,
        LogOnAccount = LogOnAccount,
        AllowDesktopInteract = AllowDesktopInteract,
        FirstFailure = FirstFailure,
        SecondFailure = SecondFailure,
        SubsequentFailure = SubsequentFailure,
        ResetFailDays = ResetFailDays,
        RestartMinutes = RestartMinutes,
        Dependencies = [.. Dependencies],
        DependentServices = [.. DependentServices],
        HasDependencyData = _dependenciesLoaded,
        HasRecoveryData = _recoveryLoaded,
        CapturedAtUtc = DateTime.UtcNow,
    };

    private async Task SaveCacheAsync(CancellationToken ct)
    {
        if (_cache == null || _propertiesCacheKey == null || _cacheData == null)
            return;

        await _cacheWriteGate.WaitAsync(ct);
        try
        {
            await _cache.SetAsync(_host, _propertiesCacheKey, _cacheData);
        }
        finally
        {
            _cacheWriteGate.Release();
        }
    }
    private async Task<bool> TryLoadCoreViaScAsync(CancellationToken ct)
    {
        try
        {
            var scResult = _isLocal
                ? await ProcessHelper.RunAsync("sc.exe", $"qc \"{_serviceName}\"", ct)
                : await ExecuteRemoteCommandAsync($"sc qc \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true, ct: ct);

            if (!scResult.Success)
            {
                var error = string.IsNullOrWhiteSpace(scResult.StdErr) ? scResult.StdOut : scResult.StdErr;
                _log.Error($"sc qc 失败: {error.Trim()}");
                return false;
            }
            ParseScOutput(scResult.StdOut, includeDependencies: false);

            var queryResult = _isLocal
                ? await ProcessHelper.RunAsync("sc.exe", $"query \"{_serviceName}\"", ct)
                : await ExecuteRemoteCommandAsync($"sc query \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true, ct: ct);
            if (queryResult.Success)
                ParseScStatus(queryResult.StdOut);

            var descResult = _isLocal
                ? await ProcessHelper.RunAsync("sc.exe", $"qdescription \"{_serviceName}\"", ct)
                : await ExecuteRemoteCommandAsync($"sc qdescription \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true, ct: ct);
            if (descResult.Success)
                ParseDescription(descResult.StdOut);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"sc 服务属性查询失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }

    /// <summary>把当前界面状态固化为“原始快照”，用于后续只提交差异。</summary>
    private void CaptureSnapshot()
    {
        _originalSnapshot = new ServiceConfigSnapshot
        {
            ServiceName = _serviceName,
            StartType = SelectedStartType,
            BinaryPath = BinaryPath,
            StartParams = StartParams,
            UseLocalSystem = UseLocalSystem,
            LogOnAccount = LogOnAccount,
            AllowDesktopInteract = AllowDesktopInteract,
            FirstFailure = FirstFailure,
            SecondFailure = SecondFailure,
            SubsequentFailure = SubsequentFailure,
            ResetFailDays = ResetFailDays,
            RestartMinutes = RestartMinutes,
        };
    }

    private ServiceConfigSnapshot BuildDesiredSnapshot() => new()
    {
        ServiceName = _serviceName,
        StartType = SelectedStartType,
        BinaryPath = BinaryPath,
        StartParams = StartParams,
        UseLocalSystem = UseLocalSystem,
        LogOnAccount = LogOnAccount,
        AllowDesktopInteract = AllowDesktopInteract,
        FirstFailure = FirstFailure,
        SecondFailure = SecondFailure,
        SubsequentFailure = SubsequentFailure,
        ResetFailDays = ResetFailDays,
        RestartMinutes = RestartMinutes,
    };

    private sealed record WmiServiceSnapshot(
        string DisplayName,
        string BinaryPath,
        string Description,
        string StartName,
        bool DesktopInteract,
        string StartType,
        string State);

    private async Task<bool> TryLoadViaWmiAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await WithWmiScopeAsync(scope =>
            {
                var escapedName = RemoteWmiHelper.EscapeWqlString(_serviceName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery(
                        "SELECT Name,DisplayName,PathName,Description,StartName,DesktopInteract," +
                        "StartMode,DelayedAutoStart,State FROM Win32_Service " +
                        $"WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null)
                    return null;

                return new WmiServiceSnapshot(
                    RemoteWmiHelper.GetString(service, "DisplayName"),
                    RemoteWmiHelper.GetString(service, "PathName"),
                    RemoteWmiHelper.GetString(service, "Description"),
                    RemoteWmiHelper.GetString(service, "StartName"),
                    RemoteWmiHelper.GetBool(service, "DesktopInteract"),
                    ServiceCommandHelper.StartTypeFromWmi(
                        RemoteWmiHelper.GetString(service, "StartMode"),
                        RemoteWmiHelper.GetBool(service, "DelayedAutoStart")),
                    RemoteWmiHelper.GetString(service, "State"));
            }, ct);

            ct.ThrowIfCancellationRequested();
            if (snapshot == null)
            {
                _log.Warn($"WMI 未找到服务: {_serviceName}");
                return false;
            }

            DisplayName = snapshot.DisplayName;
            BinaryPath = snapshot.BinaryPath;
            Description = snapshot.Description;
            AllowDesktopInteract = snapshot.DesktopInteract;

            if (snapshot.StartName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                snapshot.StartName.Equals("LocalSystemAccount", StringComparison.OrdinalIgnoreCase))
            {
                UseLocalSystem = true;
                UseThisAccount = false;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.StartName))
            {
                UseThisAccount = true;
                UseLocalSystem = false;
                LogOnAccount = snapshot.StartName;
            }

            SelectedStartType = snapshot.StartType;
            ApplyStatus(snapshot.State);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 服务属性查询失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryLoadDependenciesViaWmiAsync(CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await WithWmiScopeAsync(scope =>
            {
                var dependencies = new List<string>();
                var dependentServices = new List<string>();
                var anySuccess = false;
                var escapedName = RemoteWmiHelper.EscapeWqlString(_serviceName);

                try
                {
                    using var depSearcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery($"ASSOCIATORS OF {{Win32_Service.Name='{escapedName}'}} WHERE AssocClass=Win32_DependentService Role=Dependent"));
                    foreach (var item in depSearcher.Get().OfType<ManagementObject>())
                    {
                        var name = RemoteWmiHelper.GetString(item, "Name");
                        if (!string.IsNullOrWhiteSpace(name))
                            dependencies.Add(name);
                    }
                    anySuccess = true;
                }
                catch (Exception ex)
                {
                    _log.Debug($"WMI 依存关系查询失败: {_serviceName} - {ex.Message}");
                }

                try
                {
                    using var dependentSearcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery($"ASSOCIATORS OF {{Win32_Service.Name='{escapedName}'}} WHERE AssocClass=Win32_DependentService Role=Antecedent"));
                    foreach (var item in dependentSearcher.Get().OfType<ManagementObject>())
                    {
                        var name = RemoteWmiHelper.GetString(item, "Name");
                        if (!string.IsNullOrWhiteSpace(name))
                            dependentServices.Add(name);
                    }
                    anySuccess = true;
                }
                catch (Exception ex)
                {
                    _log.Debug($"WMI 反向依存关系查询失败: {_serviceName} - {ex.Message}");
                }

                return (Dependencies: dependencies, DependentServices: dependentServices, AnySuccess: anySuccess);
            }, ct);

            ct.ThrowIfCancellationRequested();
            if (!result.AnySuccess)
                return false;

            ReplaceDependencies(result.Dependencies, result.DependentServices);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI 依存关系加载失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }
    private void ParseScOutput(string output, bool includeDependencies = true)
    {
        foreach (var t in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
        {
            if (t.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("DISPLAY_NAME", StringComparison.OrdinalIgnoreCase))
                DisplayName = Aft(t);
            else if (t.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                BinaryPath = Aft(t);
            else if (t.StartsWith("START_TYPE", StringComparison.OrdinalIgnoreCase))
                // 交给共享解析器：`2 AUTO_START (DELAYED)` 必须优先识别 DELAYED。
                SelectedStartType = ServiceCommandHelper.ParseScStartType(Aft(t));
            else if (t.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase))
            {
                var typeStr = Aft(t).Trim();
                var firstToken = typeStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (int.TryParse(firstToken, System.Globalization.NumberStyles.HexNumber, null, out var typeVal))
                    AllowDesktopInteract = (typeVal & 0x100) != 0;
            }
            else if (t.StartsWith("SERVICE_START_NAME", StringComparison.OrdinalIgnoreCase))
            {
                var account = Aft(t);
                if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                    account.Equals("LocalSystemAccount", StringComparison.OrdinalIgnoreCase))
                {
                    UseLocalSystem = true;
                    UseThisAccount = false;
                }
                else
                {
                    UseThisAccount = true;
                    UseLocalSystem = false;
                    LogOnAccount = account;
                }
            }
            else if (includeDependencies && t.StartsWith("DEPENDENCIES", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dependency in ParseDependencyList(t))
                {
                    if (!Dependencies.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                        Dependencies.Add(dependency);
                }
            }
        }
    }

    private void ParseDescription(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output, @"(?im)^\s*DESCRIPTION\s*:\s*(.*)$");
        if (match.Success)
            Description = match.Groups[1].Value.Trim();
    }

    private static string NormalizeStartType(string? value)
    {
        var startType = (value ?? string.Empty).Trim();
        if (startType.Contains("DELAYED", StringComparison.OrdinalIgnoreCase) ||
            startType.Contains("延迟", StringComparison.OrdinalIgnoreCase))
            return ServiceCommandHelper.StartTypeAutomaticDelayed;

        if (startType.Equals("Automatic", StringComparison.OrdinalIgnoreCase) ||
            startType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return ServiceCommandHelper.StartTypeAutomatic;

        if (startType.Equals("Manual", StringComparison.OrdinalIgnoreCase) ||
            startType.Equals("Demand", StringComparison.OrdinalIgnoreCase))
            return ServiceCommandHelper.StartTypeManual;

        if (startType.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return ServiceCommandHelper.StartTypeDisabled;

        return ServiceCommandHelper.ParseScStartType(startType);
    }

    private void ParseScStatus(string output)
    {
        var stateLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase));

        ApplyStatus(stateLine == null ? output : Aft(stateLine));
    }
    private void ApplyStatus(string status)
    {
        ApplyStatus(ServiceCommandHelper.ParseState(status));
    }

    private void ApplyStatus(ServiceRuntimeState state)
    {
        switch (state)
        {
            case ServiceRuntimeState.Running:
                ServiceStatus = "运行中"; StatusColor = "#5DB872";
                CanStart = false; CanStop = true; CanPause = true; CanResume = false;
                break;
            case ServiceRuntimeState.Paused:
                ServiceStatus = "已暂停"; StatusColor = "#8F73D8";
                CanStart = false; CanStop = true; CanPause = false; CanResume = true;
                break;
            case ServiceRuntimeState.StartPending:
                ServiceStatus = "启动中"; StatusColor = "#D4A843";
                CanStart = false; CanStop = false; CanPause = false; CanResume = false;
                break;
            case ServiceRuntimeState.StopPending:
                ServiceStatus = "停止中"; StatusColor = "#D4A843";
                CanStart = false; CanStop = false; CanPause = false; CanResume = false;
                break;
            case ServiceRuntimeState.ContinuePending:
                ServiceStatus = "继续挂起"; StatusColor = "#D4A843";
                CanStart = false; CanStop = false; CanPause = false; CanResume = false;
                break;
            case ServiceRuntimeState.PausePending:
                ServiceStatus = "暂停挂起"; StatusColor = "#D4A843";
                CanStart = false; CanStop = false; CanPause = false; CanResume = false;
                break;
            case ServiceRuntimeState.Stopped:
                ServiceStatus = "已停止"; StatusColor = "#C64545";
                CanStart = true; CanStop = false; CanPause = false; CanResume = false;
                break;
        }
    }

    private void ReplaceDependencies(IEnumerable<string> dependencies, IEnumerable<string> dependentServices)
    {
        Dependencies.Clear();
        DependentServices.Clear();

        foreach (var dependency in dependencies
                     .Where(d => !string.IsNullOrWhiteSpace(d))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Dependencies.Add(dependency.Trim());

        foreach (var dependent in dependentServices
                     .Where(d => !string.IsNullOrWhiteSpace(d))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            DependentServices.Add(dependent.Trim());
    }

    private static IEnumerable<string> ParseDependencyList(string line)
    {
        var valueIndex = line.IndexOf(':');
        if (valueIndex < 0)
            return [];

        return line[(valueIndex + 1)..]
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IEnumerable<string> ParseEnumDependOutput(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = Aft(trimmed);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }
    private static string Aft(string t) => t[(t.IndexOf(':') + 1)..].Trim();

    private void ExtractStartParams()
    {
        if (string.IsNullOrWhiteSpace(BinaryPath)) return;
        var path = BinaryPath.Trim();
        if (path.Length == 0) return;

        if (path.StartsWith('"'))
        {
            var closeQuote = path.IndexOf('"', 1);
            if (closeQuote < 0) return;
            BinaryPath = path[1..closeQuote];
            StartParams = path[(closeQuote + 1)..].Trim();
        }
        else
        {
            var spaceIdx = path.IndexOf(' ');
            if (spaceIdx < 0) return;
            BinaryPath = path[..spaceIdx];
            StartParams = path[(spaceIdx + 1)..].Trim();
        }
    }

    private async Task<bool> LoadFailureActionsAsync(CancellationToken ct = default)
    {
        try
        {
            string output;
            if (_isLocal)
            {
                var result = await ProcessHelper.RunAsync("sc.exe", $"qfailure \"{_serviceName}\"", ct);
                if (!result.Success) return false;
                output = result.StdOut;
            }
            else
            {
                var result = await ExecuteRemoteCommandAsync($"sc qfailure \"{_serviceName}\"",
                    RemoteOperationKind.Inventory, silent: true, ct: ct);
                if (!result.Success) return false;
                output = result.StdOut;
            }

            ParseFailureOutput(output);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"失败恢复策略查询失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }
    private void ParseFailureOutput(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var actions = new List<(string action, int delay)>();
        int? resetSecs = null;
        bool inFailureActions = false;

        foreach (var rawLine in lines)
        {
            var t = rawLine.Trim();
            if (t.StartsWith("[SC]", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase)) continue;

            if (t.StartsWith("RESET_PERIOD", StringComparison.OrdinalIgnoreCase))
            {
                inFailureActions = false;
                var val = Aft(t).Trim();
                var spaceIdx = val.IndexOf(' ');
                if (spaceIdx > 0 && int.TryParse(val[..spaceIdx], out var secs))
                    resetSecs = secs;
            }
            else if (t.StartsWith("FAILURE_ACTIONS", StringComparison.OrdinalIgnoreCase))
            {
                inFailureActions = true;
                var actionPart = Aft(t).Trim();
                if (!string.IsNullOrWhiteSpace(actionPart) && actionPart.Contains("--"))
                    ParseActionItem(actionPart, actions);
            }
            else if (t.StartsWith("REBOOT_MESSAGE", StringComparison.OrdinalIgnoreCase)) inFailureActions = false;
            else if (t.StartsWith("COMMAND_LINE", StringComparison.OrdinalIgnoreCase)) inFailureActions = false;
            else if (inFailureActions && t.Contains("--") && t.Contains("Delay"))
            {
                ParseActionItem(t, actions);
            }
        }

        if (actions.Count > 0) FirstFailure = ActionToChinese(actions[0].action);
        if (actions.Count > 1) SecondFailure = ActionToChinese(actions[1].action);
        if (actions.Count > 2) SubsequentFailure = ActionToChinese(actions[2].action);

        if (resetSecs.HasValue)
            ResetFailDays = Math.Max(1, resetSecs.Value / 86400).ToString();

        var restartAction = actions.FirstOrDefault(a =>
            a.action.Equals("RESTART", StringComparison.OrdinalIgnoreCase));
        if (restartAction.action != null)
            RestartMinutes = Math.Max(1, restartAction.delay / 60000).ToString();
    }

    private static void ParseActionItem(string line, List<(string action, int delay)> actions)
    {
        var parts = line.Split("--", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return;
        var action = parts[0].Trim();
        var delayMatch = System.Text.RegularExpressions.Regex.Match(parts[1], @"\d+");
        var delay = delayMatch.Success ? int.Parse(delayMatch.Value) : 0;
        actions.Add((action, delay));
    }

    private static string ActionToChinese(string action) => action switch
    {
        "RESTART" => "重新启动服务",
        "RUN_COMMAND" => "运行一个程序",
        "REBOOT" => "重新启动计算机",
        _ => "不操作"
    };

    private async Task<CommandResult> RunScCommandAsync(
        string cmd,
        bool appendServiceName = true,
        CancellationToken ct = default)
    {
        var arguments = appendServiceName ? $"{cmd} \"{_serviceName}\"" : cmd;
        return _isLocal
            ? await _psExec.ExecuteLocalElevatedAsync(
                _host, _username, _password, $"sc.exe {arguments}", ct, CommandShell.Direct)
            : await ExecuteRemoteCommandAsync(
                $"sc {arguments}", RemoteOperationKind.Command, silent: true, ct: ct);
    }

    private async Task<CommandResult> ExecuteRemoteCommandAsync(
        string command,
        RemoteOperationKind operation,
        bool silent = false,
        CancellationToken ct = default)
    {
        var remoteSession = await EnsureRemoteSessionAsync(ct);
        var transportResult = await remoteSession.ExecuteAsync(
            operation,
            new RemoteCommand
            {
                TargetHost = _host,
                Username = _username,
                Password = _password,
                Command = command,
                Shell = CommandShell.Cmd,
                Silent = silent,
            },
            ct: ct);
        return transportResult.Result;
    }

    /// <summary>
    /// 属性窗口生命周期内复用一个命令会话。服务属性窗口通常会连续执行多次
    /// WMI/命令查询，重复创建会话会重复做能力探测并增加回退延迟。
    /// </summary>
    private async Task<IRemoteExecutionSession> EnsureRemoteSessionAsync(CancellationToken ct)
    {
        if (_isLocal)
            throw new InvalidOperationException("本机服务属性操作不应创建远程执行会话。");

        await _sessionGate.WaitAsync(ct);
        try
        {
            return _remoteSession ??= await _execution.CreateSessionAsync(
                _host,
                _username,
                _password,
                CapabilityProbeProfile.CommandWmiFirst,
                ct);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 复用 WMI scope，并用 gate 串行化受控访问。ManagementScope 不应在多个
    /// DCOM 调用之间无保护并发共享；连接异常时清空缓存，让下次调用重新连接。
    /// </summary>
    private async Task<T> WithWmiScopeAsync<T>(
        Func<ManagementScope, T> action,
        CancellationToken ct)
    {
        await _wmiGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var scope = _wmiScope;
            if (scope == null)
            {
                scope = RemoteWmiHelper.CreateScope(_host, _username, _password);
                await Task.Run(scope.Connect, ct);
                _wmiScope = scope;
            }

            return await Task.Run(() => action(scope), ct);
        }
        catch
        {
            _wmiScope = null;
            throw;
        }
        finally
        {
            _wmiGate.Release();
        }
    }

    /// <summary>
    /// 执行一条 sc 命令。日志只写 <paramref name="displayCommand"/>（已脱敏），
    /// 避免把 password= 明文写进日志文件。
    /// </summary>
    private async Task<bool> TryRunScCommandAsync(
        string cmd,
        string displayCommand,
        bool appendServiceName = true,
        CancellationToken ct = default)
    {
        var result = await RunScCommandAsync(cmd, appendServiceName, ct);
        if (result.Success) return true;

        var error = (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr).Trim();
        if (ServiceCommandHelper.IsServiceNotInstalled(result.ExitCode, result.StdOut + result.StdErr))
            _log.Error(ServiceCommandHelper.ServiceNotInstalledMessage(_serviceName));
        else
            _log.Error($"服务操作失败: {_serviceName} command={displayCommand} exit={result.ExitCode} {error}");
        return false;
    }

    private const int ServiceStatePollIntervalMs = 300;
    private const int ServiceStatePollTimeoutMs = 20000;

    /// <summary>
    /// 优先读取 WMI 的 State 属性，失败时才回退 `sc query`。轮询用于状态
    /// 操作，不用于配置查询；未知状态不会当作目标状态。
    /// </summary>
    private async Task<ServiceRuntimeState> TryReadServiceStateAsync(CancellationToken ct)
    {
        if (!_isLocal)
        {
            try
            {
                return await WithWmiScopeAsync(scope =>
                {
                    var escapedName = RemoteWmiHelper.EscapeWqlString(_serviceName);
                    using var searcher = new ManagementObjectSearcher(scope,
                        new ObjectQuery($"SELECT State FROM Win32_Service WHERE Name='{escapedName}'"));
                    var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                    return service == null
                        ? ServiceRuntimeState.Unknown
                        : ServiceCommandHelper.ParseState(RemoteWmiHelper.GetString(service, "State"));
                }, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Debug($"WMI 服务状态轮询失败，回退 sc query: {_serviceName} - {ex.Message}");
            }
        }

        try
        {
            var result = _isLocal
                ? await ProcessHelper.RunAsync("sc.exe", $"query \"{_serviceName}\"", ct)
                : await ExecuteRemoteCommandAsync(
                    $"sc query \"{_serviceName}\"",
                    RemoteOperationKind.Inventory,
                    silent: true,
                    ct: ct);
            return result.Success
                ? ServiceCommandHelper.ParseScQueryState(result.StdOut)
                : ServiceRuntimeState.Unknown;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"服务状态查询失败: {_serviceName} - {ex.Message}");
            return ServiceRuntimeState.Unknown;
        }
    }

    private async Task<bool> WaitForServiceStateAsync(
        ServiceRuntimeState target,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ServiceStatePollTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var state = await TryReadServiceStateAsync(ct);
            if (state != ServiceRuntimeState.Unknown)
            {
                ApplyStatus(state);
                if (state == target)
                    return true;
            }

            await Task.Delay(ServiceStatePollIntervalMs, ct);
        }

        return false;
    }

    private async Task RunStateOperationAsync(
        string command,
        string displayCommand,
        ServiceRuntimeState target,
        string actionText,
        CancellationToken ct)
    {
        if (!await TryRunScCommandAsync(command, displayCommand, ct: ct))
            return;

        _log.Info($"正在{actionText}服务: {_serviceName}");
        if (await WaitForServiceStateAsync(target, ct))
            return;

        _log.Warn(
            $"服务{actionText}命令已提交，但目标状态未在 {ServiceStatePollTimeoutMs / 1000} 秒内达到；" +
            $"已停止轮询且未重放命令: {_serviceName}");
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        await RunStateOperationAsync(
            "start", $"sc start \"{_serviceName}\"",
            ServiceRuntimeState.Running, "启动", _lifetimeCts.Token);
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        await RunStateOperationAsync(
            "stop", $"sc stop \"{_serviceName}\"",
            ServiceRuntimeState.Stopped, "停止", _lifetimeCts.Token);
    }

    [RelayCommand]
    private async Task PauseServiceAsync()
    {
        await RunStateOperationAsync(
            "pause", $"sc pause \"{_serviceName}\"",
            ServiceRuntimeState.Paused, "暂停", _lifetimeCts.Token);
    }

    [RelayCommand]
    private async Task ResumeServiceAsync()
    {
        await RunStateOperationAsync(
            "continue", $"sc continue \"{_serviceName}\"",
            ServiceRuntimeState.Running, "继续", _lifetimeCts.Token);
    }

    /// <summary>由对话框在调用 Apply/Ok 前写入密码输入并完成纯逻辑校验。</summary>
    public bool TryCaptureUserInput(string password, string confirm, out string? validationError)
    {
        validationError = ServiceCommandHelper.ValidateLogOnInput(
            UseLocalSystem, LogOnAccount, password, confirm, RequiresPasswordForAccount());
        if (validationError != null)
            return false;

        _credentials = new PasswordData(password, confirm);
        return true;
    }

    /// <summary>
    /// 只有“切换到另一个账户”才强制输入密码；账户没变时留空表示沿用目标主机上的原密码，
    /// 这样仅仅修改启动类型/失败恢复策略不需要重新输入服务密码。
    /// </summary>
    private bool RequiresPasswordForAccount()
    {
        if (UseLocalSystem)
            return false;

        var current = _originalSnapshot;
        if (current is null || current.UseLocalSystem)
            return true;

        return !string.Equals(current.LogOnAccount?.Trim(), LogOnAccount?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        await ApplyChangesAsync();
    }

    internal async Task<bool> ApplyChangesAsync()
    {
        if (IsApplying) return false;
        IsApplying = true;
        try
        {
            var desired = BuildDesiredSnapshot();
            var current = _originalSnapshot ?? desired;
            var password = UseLocalSystem ? string.Empty : _credentials.Password;
            var ct = _lifetimeCts.Token;

            // 配置变更优先走一次 WMI Change：密码作为真正的 WMI 参数传递，不经过
            // cmd/PowerShell 命令行，因此不受引号转义与 % 展开影响，也不会落在日志里。
            // 只有 WMI 无法表达的项（延迟启动、失败恢复策略）才保留命令通道。
            var wmiPlan = ServiceCommandHelper.BuildWmiChangePlan(current, desired, password);
            var wmiApplied = false;
            if (wmiPlan.HasParameters)
            {
                wmiApplied = await TryChangeServiceViaWmiAsync(wmiPlan.Parameters, ct);
                if (wmiApplied)
                    _log.Info($"已通过 WMI/DCOM 应用服务配置: {_serviceName}");
                else
                    _log.Warn($"WMI/DCOM 服务配置变更未生效，回退命令通道: {_serviceName}");
            }

            var passwordSafe = ServiceCommandHelper.IsPasswordCommandLineSafe(password, out var unsafeReason);

            foreach (var mutation in ServiceCommandHelper.BuildConfigCommands(current, desired, password))
            {
                if (IsCoveredByWmi(mutation, wmiApplied, wmiPlan))
                    continue;

                if (!passwordSafe && mutation.Kind == ServiceMutationKind.Account)
                {
                    _log.Error($"修改服务登录账户失败: {_serviceName} - {unsafeReason} 且 WMI/DCOM 通道不可用。");
                    return false;
                }

                if (!await TryRunScCommandAsync(mutation.Command, mutation.DisplayCommand,
                        appendServiceName: false, ct: ct))
                    return false;
            }

            _log.Info($"服务 {_serviceName} 属性已应用");
            await LoadAsync(ct);
            return true;
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>
    /// WMI Change 已经成功时跳过它能覆盖的项，避免同一配置被两条通道重复下发；
    /// 失败恢复策略、以及 WMI 表达不了的延迟启动仍然交给命令通道。
    /// </summary>
    private static bool IsCoveredByWmi(
        ServiceMutationCommand mutation,
        bool wmiApplied,
        WmiServiceChangePlan wmiPlan)
    {
        if (!wmiApplied)
            return false;

        return mutation.Kind switch
        {
            ServiceMutationKind.FailureActions => false,
            ServiceMutationKind.StartType when wmiPlan.RequiresScForStartType => false,
            _ => true,
        };
    }
    /// <summary>
    /// 通过 Win32_Service.Change 一次性下发服务配置变更。密码以 WMI 参数传递，
    /// 不进命令行，因此不受双引号/百分号限制，也不会出现在日志里（只记录参数名）。
    /// </summary>
    private async Task<bool> TryChangeServiceViaWmiAsync(
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        try
        {
            return await WithWmiScopeAsync(scope =>
            {
                var escapedName = RemoteWmiHelper.EscapeWqlString(_serviceName);
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT Name FROM Win32_Service WHERE Name='{escapedName}'"));
                var service = searcher.Get().OfType<ManagementObject>().FirstOrDefault();
                if (service == null) return false;

                var inParams = service.GetMethodParameters("Change");
                foreach (var pair in parameters)
                    inParams[pair.Key] = pair.Value;

                using var result = service.InvokeMethod("Change", inParams, null);
                var returnCode = RemoteWmiHelper.GetUInt32(result, "ReturnValue");
                _log.Debug($"WMI Change完成: {_serviceName} parameters=[{string.Join(',', parameters.Keys)}] return={returnCode}");
                return returnCode == 0;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug($"WMI Change失败: {_serviceName} - {ex.Message}");
            return false;
        }
    }
    [RelayCommand]
    private async Task BrowseAccountAsync()
    {
        var accounts = await QueryLocalAccountsAsync();
        if (accounts.Length == 0) return;

        var dlg = new AccountPickerDialog(_host, accounts);
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedAccount))
        {
            LogOnAccount = $".\\{dlg.SelectedAccount}";
            UseThisAccount = true;
            UseLocalSystem = false;
        }
    }

    private async Task<string[]> QueryLocalAccountsAsync()
    {
        try
        {
            if (_isLocal)
            {
                var result = await ProcessHelper.RunAsync("cmd.exe", "/c net user");
                if (!result.Success) return [];
                return ParseNetUserOutput(result.StdOut);
            }

            var psResult = await ExecuteRemoteCommandAsync(
                "cmd /c \"net user\"", RemoteOperationKind.Inventory, silent: true,
                ct: _lifetimeCts.Token);
            if (psResult.Success)
                return ParseNetUserOutput(psResult.StdOut);
        }
        catch { }
        return [];
    }

    private static string[] ParseNetUserOutput(string output)
    {
        var accounts = new List<string>();
        var inUsers = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("---")) { inUsers = true; continue; }
            if (!inUsers) continue;
            if (t.StartsWith("命令成功") || t.StartsWith("The command")) break;

            var parts = t.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
                if (part.Length > 1 && !string.IsNullOrWhiteSpace(part))
                    accounts.Add(part);
        }
        return accounts.ToArray();
    }

    [RelayCommand]
    private async Task OkAsync()
    {
        if (!await ApplyChangesAsync()) return;
        CloseRequested?.Invoke();
    }
}
