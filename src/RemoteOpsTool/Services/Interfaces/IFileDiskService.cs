using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IFileDiskService
{
    void OpenCRoot(string host, string username = "", string password = "");
    void OpenPublicDesktop(string host, string username = "", string password = "");
    void OpenDomainPublic();
    Task<List<DiskInfo>> GetDiskInfoAsync(string host, string username, string password,
        CancellationToken ct = default, bool silent = false, bool forceRefresh = false);
    Task<DiskCleanupResult> CleanupDisksAsync(string host, string username, string password,
        IEnumerable<DiskCleanupTarget> targets, IProgress<DiskCleanupProgress>? progress = null,
        CancellationToken ct = default);
    void OpenDrive(string host, string driveLetter, string username = "", string password = "");
}
