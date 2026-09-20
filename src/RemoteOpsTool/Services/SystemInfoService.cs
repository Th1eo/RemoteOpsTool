using System.Management;
using System.Text;
using System.Text.Json;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services;

public class SystemInfoService : ISystemInfoService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IRemoteExecutionService _execution;

    public SystemInfoService(
        ISettingsService settings,
        ILogService log,
        IRemoteExecutionService execution)
    {
        _settings = settings;
        _log = log;
        _execution = execution;
    }

    public async Task<string> GetSystemInfoAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        var data = await GetStructuredSystemInfoAsync(host, username, password, ct);
        if (!data.HasData) return data.RawOutput ?? data.ErrorMessage ?? "查询失败：无数据返回。";

        var sb = new StringBuilder();
        sb.AppendLine($"Host Name: {data.HostName}");
        sb.AppendLine($"OS: {data.OsCaption} ({data.OsVersion} {data.OsArchitecture})");
        sb.AppendLine($"Build: {data.BuildNumber}");
        sb.AppendLine($"Install Date: {data.InstallDate}");
        sb.AppendLine($"Boot Time: {data.LastBootUpTime}");
        sb.AppendLine($"Manufacturer: {data.Manufacturer}");
        sb.AppendLine($"Model: {data.Model}");
        sb.AppendLine($"System Type: {data.SystemType}");
        sb.AppendLine($"Processor: {data.ProcessorName}");
        sb.AppendLine($"Cores: {data.ProcessorCores} ({data.ProcessorLogicalProcessors} Logical)");
        sb.AppendLine($"Max Clock: {data.ProcessorMaxClockSpeed}");
        sb.AppendLine($"Memory: {data.TotalPhysicalMemoryGB} (Free: {data.FreePhysicalMemoryGB})");
        sb.AppendLine($"Domain: {data.Domain}");
        sb.AppendLine($"Current User: {data.CurrentUser}");
        sb.AppendLine($"Owner: {data.RegisteredOwner}");
        sb.AppendLine($"Organization: {data.RegisteredOrganization}");
        sb.AppendLine($"System Drive: {data.SystemDrive}");
        sb.AppendLine($"System Dir: {data.SystemDirectory}");
        sb.AppendLine($"Windows Dir: {data.WindowsDirectory}");
        sb.AppendLine($"Time Zone: {data.TimeZone}");
        sb.AppendLine($"BIOS: {data.BiosManufacturer} {data.BiosVersion} (S/N: {data.BiosSerialNumber})");
        sb.AppendLine($"BaseBoard: {data.BaseBoardManufacturer} {data.BaseBoardProduct} (S/N: {data.BaseBoardSerialNumber})");
        sb.AppendLine($"Graphics: {data.GraphicsCards}");
        sb.AppendLine($"GPU Drivers: {data.GpuDriverVersions}");
        sb.AppendLine($"Network Adapters: {data.NetworkAdapters}");
        sb.AppendLine($"IP Addresses: {data.IpAddresses}");
        sb.AppendLine($"MAC Addresses: {data.MacAddresses}");
        sb.AppendLine($"Logical Disks: {data.LogicalDisks}");
        sb.AppendLine($"Recent HotFixes: {data.RecentHotFixes}");
        return sb.ToString();
    }

    public async Task<SystemInfoData> GetStructuredSystemInfoAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        _log.Debug($"获取系统信息: host={host} method=WMI/DCOM user={username}");
        if (HostHelper.IsLocalHost(host))
        {
            return await QueryLocalSystemInfoAsync(ct);
        }

        var (wmiUser, wmiPassword, wmiDomain) = PrepareWmiCredentials(username, password);

        // Primary: Direct WMI via DCOM (no PsExec dependency)
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var data = await QueryRemoteWmiAsync(host, wmiUser, wmiPassword, wmiDomain, linkedCts.Token);
            if (data.HasData)
            {
                _log.Debug($"WMI 系统信息查询完成: host={host} os={data.OsCaption} version={data.OsVersion}");
                data.HostName = host;
                data.QueryMethod = "WMI";
                return data;
            }
            _log.Info($"WMI 查询未返回有效数据，回退到 PsExec...");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("WMI 查询超时，回退到 PsExec...");
        }
        catch (Exception ex)
        {
            _log.Info($"WMI 查询失败: {ex.Message}，回退到 PsExec...");
        }

        // Fallback: PsExec
        var data2 = new SystemInfoData { HostName = host };
        try
        {
            var psScript = BuildPsScript();
            var psCmd = EncodePowerShellCommand(psScript);
            _log.Debug($"回退 PsExec 获取系统信息: host={host} command={psCmd}");
            var result = await _execution.ExecuteOnceAsync(
                host, username, password, psCmd, RemoteOperationKind.Inventory, ct: ct);

            data2.RawOutput = result.StdOut;
            _log.Debug($"PsExec 系统信息结果: host={host} exit={result.ExitCode} stdout={result.StdOut} stderr={result.StdErr}");

            if (result.Success && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                var output = result.StdOut;
                var jsonStart = output.IndexOf('{');
                var jsonEnd = output.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = output[jsonStart..(jsonEnd + 1)];
                    ParseJsonIntoModel(data2, json);
                    data2.QueryMethod = "PsExec";
                }
            }

            if (!data2.HasData && !string.IsNullOrWhiteSpace(result.StdErr))
                data2.ErrorMessage = result.StdErr.Trim();
        }
        catch (Exception ex)
        {
            _log.Error($"PsExec 系统信息查询失败: {ex.Message}");
            data2.ErrorMessage = $"PsExec 查询异常: {ex.Message}";
        }

        return data2;
    }

    private static async Task<SystemInfoData> QueryLocalSystemInfoAsync(CancellationToken ct)
    {
        var data = new SystemInfoData { HostName = Environment.MachineName, QueryMethod = "WMI(local)" };
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT * FROM Win32_OperatingSystem");
                foreach (ManagementObject os in searcher.Get())
                {
                    TryGetValue(os, "Caption", v => data.OsCaption = v);
                    TryGetValue(os, "Version", v => data.OsVersion = v);
                    TryGetValue(os, "OSArchitecture", v => data.OsArchitecture = v);
                    TryGetValue(os, "BuildNumber", v => data.BuildNumber = v);
                    TryGetValue(os, "InstallDate", v => data.InstallDate = ParseDmtfDate(v));
                    TryGetValue(os, "LastBootUpTime", v => data.LastBootUpTime = ParseDmtfDate(v));
                    if (os["LastBootUpTime"] != null)
                    {
                        try
                        {
                            var boot = ManagementDateTimeConverter.ToDateTime(os["LastBootUpTime"].ToString()!);
                            data.UptimeDisplay = $"{(DateTime.Now - boot).TotalDays:F2} days";
                        }
                        catch { }
                    }
                    TryGetValue(os, "RegisteredUser", v => data.RegisteredOwner = v);
                    TryGetValue(os, "Organization", v => data.RegisteredOrganization = v);
                    TryGetValue(os, "SystemDrive", v => data.SystemDrive = v);
                    TryGetValue(os, "SystemDirectory", v => data.SystemDirectory = v);
                    TryGetValue(os, "WindowsDirectory", v => data.WindowsDirectory = v);
                    TryGetValue(os, "FreePhysicalMemory", v =>
                        data.FreePhysicalMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
                    SetUsedPhysicalMemoryFromOs(os, data);
                    TryGetValue(os, "TotalVirtualMemorySize", v =>
                        data.TotalVirtualMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
                }

                using var csSearcher = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT * FROM Win32_ComputerSystem");
                foreach (ManagementObject cs in csSearcher.Get())
                {
                    TryGetValue(cs, "Manufacturer", v => data.Manufacturer = v);
                    TryGetValue(cs, "Model", v => data.Model = v);
                    TryGetValue(cs, "SystemType", v => data.SystemType = v);
                    TryGetValue(cs, "Domain", v => data.Domain = v);
                    TryGetValue(cs, "UserName", v => data.CurrentUser = v);
                    TryGetValue(cs, "TotalPhysicalMemory", v =>
                        data.TotalPhysicalMemoryGB = $"{Convert.ToUInt64(v) / 1073741824.0:F2} GB");
                }

                using var cpuSearcher = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT * FROM Win32_Processor");
                foreach (ManagementObject cpu in cpuSearcher.Get())
                {
                    TryGetValue(cpu, "Name", v => data.ProcessorName = v);
                    TryGetValue(cpu, "NumberOfCores", v => data.ProcessorCores = v);
                    TryGetValue(cpu, "NumberOfLogicalProcessors", v => data.ProcessorLogicalProcessors = v);
                    TryGetValue(cpu, "MaxClockSpeed", v =>
                        data.ProcessorMaxClockSpeed = $"{Convert.ToInt32(v) / 1000.0:F2} GHz");
                    break;
                }

                using var biosSearcher = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT * FROM Win32_BIOS");
                foreach (ManagementObject bios in biosSearcher.Get())
                {
                    TryGetValue(bios, "Manufacturer", v => data.BiosManufacturer = v);
                    TryGetValue(bios, "SMBIOSBIOSVersion", v => data.BiosVersion = v);
                    TryGetValue(bios, "SerialNumber", v => data.BiosSerialNumber = v);
                    break;
                }

                using var tzSearcher = new ManagementObjectSearcher("root\\cimv2",
                    "SELECT * FROM Win32_TimeZone");
                foreach (ManagementObject tz in tzSearcher.Get())
                {
                    TryGetValue(tz, "Caption", v => data.TimeZone = v);
                    break;
                }

                QueryLocalBaseBoard(data);
                QueryLocalGraphics(data);
                QueryLocalNetwork(data);
                QueryLocalDisks(data);
                QueryLocalHotFixes(data);
            }, ct);
        }
        catch (Exception ex)
        {
            data.ErrorMessage = $"本地 WMI 查询失败: {ex.Message}";
        }

        return data;
    }

    private static async Task<SystemInfoData> QueryRemoteWmiAsync(string host, string username, string password,
        string domain, CancellationToken ct)
    {
        var data = new SystemInfoData();

        await Task.Run(() =>
        {
            var options = new ConnectionOptions
            {
                Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true,
                Timeout = TimeSpan.FromSeconds(25)
            };

            if (!string.IsNullOrEmpty(username))
            {
                options.Username = username;
                options.Password = password;
                if (!string.IsNullOrEmpty(domain))
                    options.Authority = $"ntlmdomain:{domain}";
            }

            var scope = new ManagementScope($"\\\\{host}\\root\\cimv2", options);
            scope.Connect();

            QueryOs(scope, data);
            QueryComputerSystem(scope, data);
            QueryProcessor(scope, data);
            QueryBios(scope, data);
            QueryTimeZone(scope, data);
            QueryBaseBoard(scope, data);
            QueryGraphics(scope, data);
            QueryNetwork(scope, data);
            QueryDisks(scope, data);
            QueryHotFixes(scope, data);
        }, ct);

        return data;
    }

    private static void QueryOs(ManagementScope scope, SystemInfoData data)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Win32_OperatingSystem"));
        foreach (ManagementObject os in searcher.Get())
        {
            TryGetValue(os, "Caption", v => data.OsCaption = v);
            TryGetValue(os, "Version", v => data.OsVersion = v);
            TryGetValue(os, "OSArchitecture", v => data.OsArchitecture = v);
            TryGetValue(os, "BuildNumber", v => data.BuildNumber = v);
            TryGetValue(os, "InstallDate", v => data.InstallDate = ParseDmtfDate(v));
            TryGetValue(os, "LastBootUpTime", v => data.LastBootUpTime = ParseDmtfDate(v));
            if (os["LastBootUpTime"] != null)
            {
                try
                {
                    var boot = ManagementDateTimeConverter.ToDateTime(os["LastBootUpTime"].ToString()!);
                    data.UptimeDisplay = $"{(DateTime.Now - boot).TotalDays:F2} days";
                }
                catch { }
            }
            TryGetValue(os, "RegisteredUser", v => data.RegisteredOwner = v);
            TryGetValue(os, "Organization", v => data.RegisteredOrganization = v);
            TryGetValue(os, "SystemDrive", v => data.SystemDrive = v);
            TryGetValue(os, "SystemDirectory", v => data.SystemDirectory = v);
            TryGetValue(os, "WindowsDirectory", v => data.WindowsDirectory = v);
            TryGetValue(os, "FreePhysicalMemory", v =>
                data.FreePhysicalMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
            SetUsedPhysicalMemoryFromOs(os, data);
            TryGetValue(os, "TotalVirtualMemorySize", v =>
                data.TotalVirtualMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
        }
    }

    private static void QueryComputerSystem(ManagementScope scope, SystemInfoData data)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Win32_ComputerSystem"));
        foreach (ManagementObject cs in searcher.Get())
        {
            TryGetValue(cs, "Manufacturer", v => data.Manufacturer = v);
            TryGetValue(cs, "Model", v => data.Model = v);
            TryGetValue(cs, "SystemType", v => data.SystemType = v);
            TryGetValue(cs, "Domain", v => data.Domain = v);
            TryGetValue(cs, "UserName", v => data.CurrentUser = v);
            TryGetValue(cs, "TotalPhysicalMemory", v =>
                data.TotalPhysicalMemoryGB = $"{Convert.ToUInt64(v) / 1073741824.0:F2} GB");
        }
    }

    private static void QueryProcessor(ManagementScope scope, SystemInfoData data)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Win32_Processor"));
        foreach (ManagementObject cpu in searcher.Get())
        {
            TryGetValue(cpu, "Name", v => data.ProcessorName = v);
            TryGetValue(cpu, "NumberOfCores", v => data.ProcessorCores = v);
            TryGetValue(cpu, "NumberOfLogicalProcessors", v => data.ProcessorLogicalProcessors = v);
            TryGetValue(cpu, "MaxClockSpeed", v =>
                data.ProcessorMaxClockSpeed = $"{Convert.ToInt32(v) / 1000.0:F2} GHz");
            break;
        }
    }

    private static void QueryBios(ManagementScope scope, SystemInfoData data)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Win32_BIOS"));
        foreach (ManagementObject bios in searcher.Get())
        {
            TryGetValue(bios, "Manufacturer", v => data.BiosManufacturer = v);
            TryGetValue(bios, "SMBIOSBIOSVersion", v => data.BiosVersion = v);
            TryGetValue(bios, "SerialNumber", v => data.BiosSerialNumber = v);
            break;
        }
    }

    private static void QueryTimeZone(ManagementScope scope, SystemInfoData data)
    {
        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Win32_TimeZone"));
        foreach (ManagementObject tz in searcher.Get())
        {
            TryGetValue(tz, "Caption", v => data.TimeZone = v);
            break;
        }
    }

    private static void QueryBaseBoard(ManagementScope scope, SystemInfoData data)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Manufacturer,Product,SerialNumber FROM Win32_BaseBoard"));
            foreach (ManagementObject board in searcher.Get())
            {
                TryGetValue(board, "Manufacturer", v => data.BaseBoardManufacturer = v);
                TryGetValue(board, "Product", v => data.BaseBoardProduct = v);
                TryGetValue(board, "SerialNumber", v => data.BaseBoardSerialNumber = v);
                break;
            }
        }
        catch { }
    }

    private static void QueryGraphics(ManagementScope scope, SystemInfoData data)
    {
        try
        {
            var names = new List<string>();
            var drivers = new List<string>();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name,DriverVersion FROM Win32_VideoController"));
            foreach (ManagementObject gpu in searcher.Get())
            {
                var name = GetManagementValue(gpu, "Name");
                if (string.IsNullOrWhiteSpace(name) || IsVirtualDisplayAdapter(name))
                    continue;

                names.Add(name);
                var driver = GetManagementValue(gpu, "DriverVersion");
                if (!string.IsNullOrWhiteSpace(driver))
                    drivers.Add($"{name}: {driver}");
            }

            data.GraphicsCards = JoinDistinct(names);
            data.GpuDriverVersions = JoinDistinct(drivers);
        }
        catch { }
    }

    private static void QueryNetwork(ManagementScope scope, SystemInfoData data)
    {
        try
        {
            var adapters = new List<string>();
            var ips = new List<string>();
            var macs = new List<string>();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Description,IPAddress,MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"));
            foreach (ManagementObject nic in searcher.Get())
            {
                var description = GetManagementValue(nic, "Description");
                if (!string.IsNullOrWhiteSpace(description))
                    adapters.Add(description);

                if (nic["IPAddress"] is string[] addressList)
                    ips.AddRange(addressList.Where(ip => !string.IsNullOrWhiteSpace(ip)));

                var mac = GetManagementValue(nic, "MACAddress");
                if (!string.IsNullOrWhiteSpace(mac))
                    macs.Add(mac);
            }

            data.NetworkAdapters = JoinDistinct(adapters);
            data.IpAddresses = JoinDistinct(ips);
            data.MacAddresses = JoinDistinct(macs);
        }
        catch { }
    }

    private static void QueryDisks(ManagementScope scope, SystemInfoData data)
    {
        try
        {
            var disks = new List<string>();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT DeviceID,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"));
            foreach (ManagementObject disk in searcher.Get())
            {
                var id = GetManagementValue(disk, "DeviceID");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var size = FormatBytes(GetManagementValue(disk, "Size"));
                var free = FormatBytes(GetManagementValue(disk, "FreeSpace"));
                disks.Add($"{id} free {free} / total {size}");
            }

            data.LogicalDisks = JoinDistinct(disks);
        }
        catch { }
    }

    private static void QueryHotFixes(ManagementScope scope, SystemInfoData data)
    {
        try
        {
            var fixes = new List<(DateTime date, string text)>();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT HotFixID,Description,InstalledOn FROM Win32_QuickFixEngineering"));
            foreach (ManagementObject fix in searcher.Get())
            {
                var id = GetManagementValue(fix, "HotFixID");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var description = GetManagementValue(fix, "Description");
                var installedOn = GetManagementValue(fix, "InstalledOn");
                var date = ParseLooseDate(installedOn);
                var suffix = string.IsNullOrWhiteSpace(installedOn) ? description : $"{description} {installedOn}";
                fixes.Add((date, $"{id} {suffix}".Trim()));
            }

            data.RecentHotFixes = string.Join("; ", fixes
                .OrderByDescending(item => item.date)
                .Take(5)
                .Select(item => item.text));
        }
        catch { }
    }

    private static void QueryLocalBaseBoard(SystemInfoData data)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\cimv2",
                "SELECT Manufacturer,Product,SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject board in searcher.Get())
            {
                TryGetValue(board, "Manufacturer", v => data.BaseBoardManufacturer = v);
                TryGetValue(board, "Product", v => data.BaseBoardProduct = v);
                TryGetValue(board, "SerialNumber", v => data.BaseBoardSerialNumber = v);
                break;
            }
        }
        catch { }
    }

    private static void QueryLocalGraphics(SystemInfoData data)
    {
        try
        {
            var names = new List<string>();
            var drivers = new List<string>();
            using var searcher = new ManagementObjectSearcher("root\\cimv2",
                "SELECT Name,DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject gpu in searcher.Get())
            {
                var name = GetManagementValue(gpu, "Name");
                if (string.IsNullOrWhiteSpace(name) || IsVirtualDisplayAdapter(name))
                    continue;

                names.Add(name);
                var driver = GetManagementValue(gpu, "DriverVersion");
                if (!string.IsNullOrWhiteSpace(driver))
                    drivers.Add($"{name}: {driver}");
            }

            data.GraphicsCards = JoinDistinct(names);
            data.GpuDriverVersions = JoinDistinct(drivers);
        }
        catch { }
    }

    private static void QueryLocalNetwork(SystemInfoData data)
    {
        try
        {
            var adapters = new List<string>();
            var ips = new List<string>();
            var macs = new List<string>();
            using var searcher = new ManagementObjectSearcher("root\\cimv2",
                "SELECT Description,IPAddress,MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (ManagementObject nic in searcher.Get())
            {
                var description = GetManagementValue(nic, "Description");
                if (!string.IsNullOrWhiteSpace(description))
                    adapters.Add(description);

                if (nic["IPAddress"] is string[] addressList)
                    ips.AddRange(addressList.Where(ip => !string.IsNullOrWhiteSpace(ip)));

                var mac = GetManagementValue(nic, "MACAddress");
                if (!string.IsNullOrWhiteSpace(mac))
                    macs.Add(mac);
            }

            data.NetworkAdapters = JoinDistinct(adapters);
            data.IpAddresses = JoinDistinct(ips);
            data.MacAddresses = JoinDistinct(macs);
        }
        catch { }
    }

    private static void QueryLocalDisks(SystemInfoData data)
    {
        try
        {
            var disks = new List<string>();
            using var searcher = new ManagementObjectSearcher("root\\cimv2",
                "SELECT DeviceID,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            foreach (ManagementObject disk in searcher.Get())
            {
                var id = GetManagementValue(disk, "DeviceID");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var size = FormatBytes(GetManagementValue(disk, "Size"));
                var free = FormatBytes(GetManagementValue(disk, "FreeSpace"));
                disks.Add($"{id} free {free} / total {size}");
            }

            data.LogicalDisks = JoinDistinct(disks);
        }
        catch { }
    }

    private static void QueryLocalHotFixes(SystemInfoData data)
    {
        try
        {
            var fixes = new List<(DateTime date, string text)>();
            using var searcher = new ManagementObjectSearcher("root\\cimv2",
                "SELECT HotFixID,Description,InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject fix in searcher.Get())
            {
                var id = GetManagementValue(fix, "HotFixID");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var description = GetManagementValue(fix, "Description");
                var installedOn = GetManagementValue(fix, "InstalledOn");
                var date = ParseLooseDate(installedOn);
                var suffix = string.IsNullOrWhiteSpace(installedOn) ? description : $"{description} {installedOn}";
                fixes.Add((date, $"{id} {suffix}".Trim()));
            }

            data.RecentHotFixes = string.Join("; ", fixes
                .OrderByDescending(item => item.date)
                .Take(5)
                .Select(item => item.text));
        }
        catch { }
    }

    private static void SetUsedPhysicalMemoryFromOs(ManagementObject os, SystemInfoData data)
    {
        try
        {
            if (os["TotalVisibleMemorySize"] == null || os["FreePhysicalMemory"] == null)
                return;

            var totalKb = Convert.ToInt64(os["TotalVisibleMemorySize"]);
            var freeKb = Convert.ToInt64(os["FreePhysicalMemory"]);
            var usedKb = Math.Max(0, totalKb - freeKb);
            data.UsedPhysicalMemoryGB = $"{usedKb / 1048576.0:F2} GB";
        }
        catch { }
    }

    private static string GetManagementValue(ManagementObject obj, string name)
    {
        try
        {
            return obj[name]?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string JoinDistinct(IEnumerable<string> values)
    {
        return string.Join("; ", values
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsVirtualDisplayAdapter(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("dameware")
               || lower.Contains("replicator")
               || lower.Contains("mirror")
               || lower.Contains("virtual")
               || lower.Contains("remote display")
               || lower.Contains("basic render");
    }

    private static string FormatBytes(string value)
    {
        if (!double.TryParse(value, out var bytes))
            return string.Empty;

        return $"{bytes / 1073741824.0:F2} GB";
    }

    private static DateTime ParseLooseDate(string value)
    {
        if (DateTime.TryParse(value, out var parsed))
            return parsed;

        return DateTime.MinValue;
    }

    private static (string user, string password, string domain) PrepareWmiCredentials(
        string username, string password)
    {
        var domain = string.Empty;
        var user = username;

        if (!string.IsNullOrEmpty(username))
        {
            var lastBackslash = username.LastIndexOf('\\');
            if (lastBackslash >= 0 && lastBackslash < username.Length - 1)
            {
                domain = username[..lastBackslash];
                user = username[(lastBackslash + 1)..];
            }
            else
            {
                var atIndex = username.IndexOf('@');
                if (atIndex > 0)
                {
                    user = username[..atIndex];
                    domain = string.Empty;
                }
            }
        }

        return (user, password, domain);
    }

    private static void TryGetValue(ManagementObject obj, string name, Action<string> setter)
    {
        try
        {
            var value = obj[name]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                setter(value);
        }
        catch { }
    }

    private static string ParseDmtfDate(string dmtfDate)
    {
        try
        {
            return ManagementDateTimeConverter.ToDateTime(dmtfDate).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch { return dmtfDate; }
    }

    public static string EncodePowerShellCommand(string script)
    {
        var bytes = Encoding.Unicode.GetBytes(script);
        var base64 = Convert.ToBase64String(bytes);
        return $"powershell -NoProfile -EncodedCommand {base64}";
    }

    private static string BuildPsScript()
    {
        return @"
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$bios = Get-CimInstance Win32_BIOS
$tz = Get-CimInstance Win32_TimeZone
$board = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
$gpu = Get-CimInstance Win32_VideoController | Where-Object {
    $_.Name -and $_.Name -notmatch 'DameWare|Replicator|Mirror|Virtual|Remote Display|Basic Render'
}
$net = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True'
$disk = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'
$hotfix = Get-CimInstance Win32_QuickFixEngineering | Sort-Object InstalledOn -Descending | Select-Object -First 5
$osArch = if ($os.OSArchitecture) { $os.OSArchitecture } else { $env:PROCESSOR_ARCHITECTURE }
$totalMem = [math]::Round($cs.TotalPhysicalMemory/1GB,2)
$freeMem = [math]::Round($os.FreePhysicalMemory/1MB,2)
$usedMem = [math]::Round($totalMem-$freeMem,2)
$totalVMem = [math]::Round($os.TotalVirtualMemorySize/1MB,2)
$installDate = if ($os.InstallDate) { $os.InstallDate.ToString('yyyy-MM-dd HH:mm:ss') } else { '' }
$bootTime = if ($os.LastBootUpTime) { $os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm:ss') } else { '' }
$uptime = if ($os.LastBootUpTime) { [math]::Round(((Get-Date)-$os.LastBootUpTime).TotalDays,2).ToString()+' days' } else { '' }
$clockSpeed = if ($cpu.MaxClockSpeed) { ([math]::Round($cpu.MaxClockSpeed/1000,2)).ToString()+' GHz' } else { '' }
$gpuNames = ($gpu | ForEach-Object { $_.Name }) -join '; '
$gpuDrivers = ($gpu | ForEach-Object { if ($_.DriverVersion) { $_.Name + ': ' + $_.DriverVersion } }) -join '; '
$netNames = ($net | ForEach-Object { $_.Description }) -join '; '
$ipAddresses = ($net | ForEach-Object { $_.IPAddress } | Where-Object { $_ }) -join '; '
$macAddresses = ($net | ForEach-Object { $_.MACAddress } | Where-Object { $_ }) -join '; '
$logicalDisks = ($disk | ForEach-Object { $_.DeviceID + ' free ' + ([math]::Round($_.FreeSpace/1GB,2)).ToString() + ' GB / total ' + ([math]::Round($_.Size/1GB,2)).ToString() + ' GB' }) -join '; '
$recentHotFixes = ($hotfix | ForEach-Object { ($_.HotFixID + ' ' + $_.Description + ' ' + $_.InstalledOn).Trim() }) -join '; '
@{
    HostName=$env:COMPUTERNAME;
    OsCaption=$os.Caption;
    OsVersion=$os.Version;
    OsArchitecture=$osArch;
    BuildNumber=$os.BuildNumber;
    InstallDate=$installDate;
    LastBootUpTime=$bootTime;
    UptimeDisplay=$uptime;
    Manufacturer=$cs.Manufacturer;
    Model=$cs.Model;
    TotalPhysicalMemoryGB=$totalMem.ToString()+' GB';
    FreePhysicalMemoryGB=$freeMem.ToString()+' GB';
    UsedPhysicalMemoryGB=$usedMem.ToString()+' GB';
    ProcessorName=$cpu.Name;
    ProcessorCount=$cpu.NumberOfCores.ToString()+' cores';
    ProcessorCores=$cpu.NumberOfCores.ToString();
    ProcessorLogicalProcessors=$cpu.NumberOfLogicalProcessors.ToString();
    ProcessorMaxClockSpeed=$clockSpeed;
    SystemType=$cs.SystemType;
    Domain=$cs.Domain;
    RegisteredOwner=$os.RegisteredUser;
    RegisteredOrganization=$os.Organization;
    SystemDrive=$os.SystemDrive;
    SystemDirectory=$os.SystemDirectory;
    WindowsDirectory=$os.WindowsDirectory;
    TimeZone=$tz.Caption;
    CurrentUser=$cs.UserName;
    BiosManufacturer=$bios.Manufacturer;
    BiosVersion=$bios.SMBIOSBIOSVersion;
    BiosSerialNumber=$bios.SerialNumber;
    BaseBoardManufacturer=$board.Manufacturer;
    BaseBoardProduct=$board.Product;
    BaseBoardSerialNumber=$board.SerialNumber;
    GraphicsCards=$gpuNames;
    GpuDriverVersions=$gpuDrivers;
    NetworkAdapters=$netNames;
    IpAddresses=$ipAddresses;
    MacAddresses=$macAddresses;
    LogicalDisks=$logicalDisks;
    RecentHotFixes=$recentHotFixes;
    TotalVirtualMemoryGB=$totalVMem.ToString()+' GB'
} | ConvertTo-Json -Compress
";
    }

    private static void ParseJsonIntoModel(SystemInfoData data, string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            data.OsCaption = GetProp(parsed, "OsCaption");
            data.OsVersion = GetProp(parsed, "OsVersion");
            data.OsArchitecture = GetProp(parsed, "OsArchitecture");
            data.BuildNumber = GetProp(parsed, "BuildNumber");
            data.InstallDate = GetProp(parsed, "InstallDate");
            data.LastBootUpTime = GetProp(parsed, "LastBootUpTime");
            data.UptimeDisplay = GetProp(parsed, "UptimeDisplay");
            data.Manufacturer = GetProp(parsed, "Manufacturer");
            data.Model = GetProp(parsed, "Model");
            data.TotalPhysicalMemoryGB = GetProp(parsed, "TotalPhysicalMemoryGB");
            data.FreePhysicalMemoryGB = GetProp(parsed, "FreePhysicalMemoryGB");
            data.UsedPhysicalMemoryGB = GetProp(parsed, "UsedPhysicalMemoryGB");
            data.ProcessorName = GetProp(parsed, "ProcessorName");
            data.ProcessorCount = GetProp(parsed, "ProcessorCount");
            data.ProcessorCores = GetProp(parsed, "ProcessorCores");
            data.ProcessorLogicalProcessors = GetProp(parsed, "ProcessorLogicalProcessors");
            data.ProcessorMaxClockSpeed = GetProp(parsed, "ProcessorMaxClockSpeed");
            data.SystemType = GetProp(parsed, "SystemType");
            data.Domain = GetProp(parsed, "Domain");
            data.RegisteredOwner = GetProp(parsed, "RegisteredOwner");
            data.RegisteredOrganization = GetProp(parsed, "RegisteredOrganization");
            data.SystemDrive = GetProp(parsed, "SystemDrive");
            data.SystemDirectory = GetProp(parsed, "SystemDirectory");
            data.WindowsDirectory = GetProp(parsed, "WindowsDirectory");
            data.TimeZone = GetProp(parsed, "TimeZone");
            data.CurrentUser = GetProp(parsed, "CurrentUser");
            data.BiosManufacturer = GetProp(parsed, "BiosManufacturer");
            data.BiosVersion = GetProp(parsed, "BiosVersion");
            data.BiosSerialNumber = GetProp(parsed, "BiosSerialNumber");
            data.BaseBoardManufacturer = GetProp(parsed, "BaseBoardManufacturer");
            data.BaseBoardProduct = GetProp(parsed, "BaseBoardProduct");
            data.BaseBoardSerialNumber = GetProp(parsed, "BaseBoardSerialNumber");
            data.GraphicsCards = GetProp(parsed, "GraphicsCards");
            data.GpuDriverVersions = GetProp(parsed, "GpuDriverVersions");
            data.NetworkAdapters = GetProp(parsed, "NetworkAdapters");
            data.IpAddresses = GetProp(parsed, "IpAddresses");
            data.MacAddresses = GetProp(parsed, "MacAddresses");
            data.LogicalDisks = GetProp(parsed, "LogicalDisks");
            data.RecentHotFixes = GetProp(parsed, "RecentHotFixes");
            data.TotalVirtualMemoryGB = GetProp(parsed, "TotalVirtualMemoryGB");
        }
        catch { }
    }

    private static string GetProp(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null)
            return prop.GetString() ?? string.Empty;
        return string.Empty;
    }
}
