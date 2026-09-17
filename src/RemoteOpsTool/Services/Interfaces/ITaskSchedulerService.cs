using RemoteOpsTool.Models;

namespace RemoteOpsTool.Services.Interfaces;

/// <summary>
/// Native Task Scheduler (schtasks.exe /s) transport. This channel reaches the
/// target through the Task Scheduler RPC (MS-TSCH) protocol and needs neither
/// WMI/DCOM nor WinRM nor the PsExec SMB service. It is a fire-and-forget
/// channel: it reports the task's LastTaskResult but does not stream or capture
/// the remote command's standard output.
/// </summary>
public interface ITaskSchedulerService
{
    Task<CommandResult> ExecuteAsync(
        string targetHost,
        string username,
        string password,
        string command,
        CancellationToken ct = default);

    Task<CommandResult> ExecuteInteractiveAsync(
        string targetHost,
        string username,
        string password,
        string command,
        string desktopUsername,
        CancellationToken ct = default);
}
