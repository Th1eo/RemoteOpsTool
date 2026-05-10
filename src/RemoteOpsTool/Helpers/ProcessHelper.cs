using System.Diagnostics;
using System.Security;

namespace RemoteOpsTool.Helpers;

public static class ProcessHelper
{
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default)
    {
        return await RunAsync(fileName, arguments, null, null, null, ct);
    }

    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (!string.IsNullOrEmpty(runAsUser) && !string.IsNullOrEmpty(runAsPassword))
        {
            process.StartInfo.UserName = runAsUser;
            process.StartInfo.Password = ToSecureString(runAsPassword);
            process.StartInfo.Domain = runAsDomain ?? string.Empty;
            process.StartInfo.WorkingDirectory = Environment.SystemDirectory;
        }

        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        try
        {
            if (!process.Start())
            {
                var msg = !File.Exists(fileName)
                    ? $"Failed to start process. File not found: {fileName}"
                    : $"Failed to start process (runAs: {!string.IsNullOrEmpty(runAsUser)}).";
                return new CommandResult(-1, string.Empty, msg);
            }
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty, ex.Message);
        }

        ct.Register(() =>
        {
            try { process.Kill(true); } catch { }
            tcs.TrySetCanceled();
        });

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await tcs.Task;
        if (ct.IsCancellationRequested)
        {
            process.Kill(true);
            ct.ThrowIfCancellationRequested();
        }

        return new CommandResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    public static async Task RunWithOutputAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        CancellationToken ct = default)
    {
        await RunWithOutputAsync(fileName, arguments, onOutputLine, null, null, null, ct);
    }

    public static async Task RunWithOutputAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (!string.IsNullOrEmpty(runAsUser) && !string.IsNullOrEmpty(runAsPassword))
        {
            process.StartInfo.UserName = runAsUser;
            process.StartInfo.Password = ToSecureString(runAsPassword);
            process.StartInfo.Domain = runAsDomain ?? string.Empty;
            process.StartInfo.WorkingDirectory = Environment.SystemDirectory;
        }

        try
        {
            if (!process.Start()) return;
        }
        catch { return; }

        using var ctr = ct.Register(() =>
        {
            try { process.Kill(true); } catch { }
        });

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
                onOutputLine(line);
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) != null)
                onOutputLine(line);
        }, ct);

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
    }

    private static SecureString ToSecureString(string password)
    {
        var ss = new SecureString();
        foreach (var c in password) ss.AppendChar(c);
        return ss;
    }

    public static (string User, string Domain) SplitUserDomain(string username)
    {
        var lastBackslash = username.LastIndexOf('\\');
        if (lastBackslash >= 0 && lastBackslash < username.Length - 1)
            return (username[(lastBackslash + 1)..], username[..lastBackslash]);
        return (username, string.Empty);
    }
}
