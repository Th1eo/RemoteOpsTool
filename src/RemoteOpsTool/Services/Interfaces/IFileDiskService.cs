using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

public interface IFileDiskService
{
    void OpenCRoot(string host, string username = "", string password = "");
    void OpenPublicDesktop(string host, string username = "", string password = "");
    void OpenDomainPublic();
    Task<List<DiskInfo>> GetDiskInfoAsync(string host, string username, string password, CancellationToken ct = default, bool silent = false);
    Task CleanupDisksAsync(string host, string username, string password, IEnumerable<string> directories, CancellationToken ct = default);
    void OpenDrive(string host, string driveLetter, string username = "", string password = "");
}
