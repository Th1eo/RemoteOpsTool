using System.Management;
using System.Text;
using System.Text.Json;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class SystemInfoService : ISystemInfoService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IPsExecService _psExec;

    public SystemInfoService(ISettingsService settings, ILogService log, IPsExecService psExec)
    {
        _settings = settings;
        _log = log;
        _psExec = psExec;
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
        sb.AppendLine($"Owner: {data.RegisteredOwner}");
        sb.AppendLine($"Organization: {data.RegisteredOrganization}");
        sb.AppendLine($"System Drive: {data.SystemDrive}");
        sb.AppendLine($"Windows Dir: {data.WindowsDirectory}");
        sb.AppendLine($"Time Zone: {data.TimeZone}");
        sb.AppendLine($"BIOS: {data.BiosVersion} (S/N: {data.BiosSerialNumber})");
        return sb.ToString();
    }

    public async Task<SystemInfoData> GetStructuredSystemInfoAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
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
            var result = await _psExec.ExecuteAsync(host, username, password, psCmd, silent: false, ct: ct);

            data2.RawOutput = result.StdOut;

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
                    TryGetValue(os, "WindowsDirectory", v => data.WindowsDirectory = v);
                    TryGetValue(os, "FreePhysicalMemory", v =>
                        data.FreePhysicalMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
                    TryGetValue(os, "TotalVisibleMemorySize", v =>
                        data.UsedPhysicalMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
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
            TryGetValue(os, "WindowsDirectory", v => data.WindowsDirectory = v);
            TryGetValue(os, "FreePhysicalMemory", v =>
                data.FreePhysicalMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
            TryGetValue(os, "TotalVisibleMemorySize", v =>
                data.UsedPhysicalMemoryGB = $"{Convert.ToInt64(v) / 1048576.0:F2} GB");
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
$osArch = if ($os.OSArchitecture) { $os.OSArchitecture } else { $env:PROCESSOR_ARCHITECTURE }
$totalMem = [math]::Round($cs.TotalPhysicalMemory/1GB,2)
$freeMem = [math]::Round($os.FreePhysicalMemory/1MB,2)
$usedMem = [math]::Round($totalMem-$freeMem,2)
$totalVMem = [math]::Round($os.TotalVirtualMemorySize/1MB,2)
$installDate = if ($os.InstallDate) { $os.InstallDate.ToString('yyyy-MM-dd HH:mm:ss') } else { '' }
$bootTime = if ($os.LastBootUpTime) { $os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm:ss') } else { '' }
$uptime = if ($os.LastBootUpTime) { [math]::Round(((Get-Date)-$os.LastBootUpTime).TotalDays,2).ToString()+' days' } else { '' }
$clockSpeed = if ($cpu.MaxClockSpeed) { ([math]::Round($cpu.MaxClockSpeed/1000,2)).ToString()+' GHz' } else { '' }
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
    WindowsDirectory=$os.WindowsDirectory;
    TimeZone=$tz.Caption;
    BiosVersion=$bios.SMBIOSBIOSVersion;
    BiosSerialNumber=$bios.SerialNumber;
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
            data.WindowsDirectory = GetProp(parsed, "WindowsDirectory");
            data.TimeZone = GetProp(parsed, "TimeZone");
            data.BiosVersion = GetProp(parsed, "BiosVersion");
            data.BiosSerialNumber = GetProp(parsed, "BiosSerialNumber");
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
