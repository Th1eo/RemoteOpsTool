using System.Diagnostics;
using System.Runtime.InteropServices;
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
        IReadOnlyList<string> arguments,
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
        return await RunAsync(fileName, arguments, null, runAsUser, runAsPassword, runAsDomain, ct);
    }

    public static async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        return await RunAsync(fileName, null, arguments, runAsUser, runAsPassword, runAsDomain, ct);
    }

    private static async Task<CommandResult> RunAsync(
        string fileName,
        string? arguments,
        IReadOnlyList<string>? argumentList,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (argumentList != null)
        {
            process.StartInfo.Arguments = string.Empty;
            foreach (var argument in argumentList)
                process.StartInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(runAsUser) && !string.IsNullOrEmpty(runAsPassword))
        {
            process.StartInfo.UserName = runAsUser;
            process.StartInfo.Password = ToSecureString(runAsPassword);
            process.StartInfo.Domain = runAsDomain ?? string.Empty;
            process.StartInfo.WorkingDirectory = Environment.SystemDirectory;
            process.StartInfo.LoadUserProfile = true;
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
        IReadOnlyList<string> arguments,
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
        await RunWithOutputAsync(fileName, arguments, null, onOutputLine, runAsUser, runAsPassword, runAsDomain, ct);
    }

    public static async Task RunWithOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        await RunWithOutputAsync(fileName, null, arguments, onOutputLine, runAsUser, runAsPassword, runAsDomain, ct);
    }

    private static async Task RunWithOutputAsync(
        string fileName,
        string? arguments,
        IReadOnlyList<string>? argumentList,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        if (argumentList != null)
        {
            process.StartInfo.Arguments = string.Empty;
            foreach (var argument in argumentList)
                process.StartInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(runAsUser) && !string.IsNullOrEmpty(runAsPassword))
        {
            process.StartInfo.UserName = runAsUser;
            process.StartInfo.Password = ToSecureString(runAsPassword);
            process.StartInfo.Domain = runAsDomain ?? string.Empty;
            process.StartInfo.WorkingDirectory = Environment.SystemDirectory;
            process.StartInfo.LoadUserProfile = true;
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

    public static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];

        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero)
            return [commandLine];

        try
        {
            var args = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                var ptr = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(ptr) ?? string.Empty;
            }
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
        out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
