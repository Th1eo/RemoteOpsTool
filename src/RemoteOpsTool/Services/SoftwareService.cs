using RemoteOpsTool.Constants;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Interfaces;

namespace RemoteOpsTool.Services;

public class SoftwareService : ISoftwareService
{
    private readonly IPsExecService _psExec;
    private readonly ILogService _log;

    public SoftwareService(IPsExecService psExec, ILogService log)
    {
        _psExec = psExec;
        _log = log;
    }

    public async Task<List<SoftwareInfo>> GetInstalledSoftwareAsync(string host, string username, string password,
        CancellationToken ct = default)
    {
        var software = new List<SoftwareInfo>();
        foreach (var regKey in AppConstants.SoftwareRegistryKeys)
        {
            var psCommand = $"powershell \"Get-ItemProperty '{regKey}\\*' -ErrorAction SilentlyContinue | Where-Object {{ $_.DisplayName }} | Select-Object DisplayName,UninstallString,Publisher,InstallLocation,PSChildName | ConvertTo-Csv -NoTypeInformation\"";

            var result = await _psExec.ExecuteAsync(host, username, password, psCommand, ct: ct);
            if (!result.Success) continue;

            var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("\"DisplayName\"")) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                var childName = parts[4].Trim('"');
                software.Add(new SoftwareInfo
                {
                    DisplayName = parts[0].Trim('"'),
                    UninstallString = parts[1].Trim('"'),
                    Publisher = parts[2].Trim('"'),
                    InstallLocation = parts[3].Trim('"'),
                    RegistryKey = $"{regKey}\\{childName}"
                });
            }
        }

        return software.DistinctBy(s => s.DisplayName).ToList();
    }

    public async Task<bool> UninstallSilentlyAsync(string host, string username, string password,
        string uninstallString, CancellationToken ct = default)
    {
        var result = await _psExec.ExecuteAsync(host, username, password, uninstallString, ct: ct);
        return result.Success;
    }

    public async Task<bool> UninstallInteractiveAsync(string host, string username, string password,
        string uninstallString, int sessionId, CancellationToken ct = default)
    {
        var result = await _psExec.ExecuteAsync(host, username, password, uninstallString,
            interactiveSession: true, sessionId: sessionId, ct: ct);
        return result.Success;
    }
}
