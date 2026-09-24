using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace RemoteOpsTool.Helpers;

public static class ProcessHelper
{
    private const int LogonNetCredentialsOnly = 0x00000002;
    public const string LocalStartFailurePrefix = "本地启动失败：";

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
        return await RunAsync(fileName, arguments, null, runAsUser, runAsPassword, runAsDomain, null, ct);
    }

    public static async Task<CommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        return await RunAsync(fileName, null, arguments, runAsUser, runAsPassword, runAsDomain, null, ct);
    }

    public static async Task<CommandResult> RunAsyncWithEnvironment(
        string fileName,
        IReadOnlyList<string> arguments,
        string runAsUser,
        string runAsPassword,
        string? runAsDomain,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken ct = default)
    {
        return await RunAsync(
            fileName, null, arguments, runAsUser, runAsPassword, runAsDomain, environmentVariables, ct);
    }

    private static async Task<CommandResult> RunAsync(
        string fileName,
        string? arguments,
        IReadOnlyList<string>? argumentList,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var hasRunAsCredentials = !string.IsNullOrEmpty(runAsUser) && !string.IsNullOrEmpty(runAsPassword);
        if (argumentList != null)
        {
            // ProcessStartInfo.ArgumentList cannot be used together with UserName.
            // RunAs uses CreateProcessWithLogonW internally, so provide one correctly
            // quoted command-line string when launching under alternate credentials.
            if (hasRunAsCredentials)
                process.StartInfo.Arguments = CombineArgumentsForWindows(argumentList);
            else
            {
                process.StartInfo.Arguments = string.Empty;
                foreach (var argument in argumentList)
                    process.StartInfo.ArgumentList.Add(argument);
            }
        }

        if (hasRunAsCredentials)
        {
            process.StartInfo.UserName = runAsUser;
            process.StartInfo.Password = ToSecureString(runAsPassword!);
            process.StartInfo.Domain = runAsDomain ?? string.Empty;
            process.StartInfo.WorkingDirectory = Environment.SystemDirectory;
            process.StartInfo.LoadUserProfile = true;
        }

        if (environmentVariables != null)
        {
            foreach (var (name, value) in environmentVariables)
                process.StartInfo.Environment[name] = value;
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
                return new CommandResult(-1, string.Empty, LocalStartFailurePrefix + msg);
            }
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty, LocalStartFailurePrefix + ex.Message);
        }

        try { process.StandardInput.Close(); } catch { }

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

    public static async Task<CommandResult> RunWithOutputAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        CancellationToken ct = default)
    {
        return await RunWithOutputAsync(fileName, arguments, null, onOutputLine, null, null, null, null, ct);
    }

    public static async Task<CommandResult> RunWithOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string> onOutputLine,
        CancellationToken ct = default)
    {
        return await RunWithOutputAsync(fileName, null, arguments, onOutputLine, null, null, null, null, ct);
    }

    public static async Task<CommandResult> RunWithOutputAsync(
        string fileName,
        string arguments,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        return await RunWithOutputAsync(fileName, arguments, null, onOutputLine, runAsUser, runAsPassword, runAsDomain, null, ct);
    }

    public static async Task<CommandResult> RunWithOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        CancellationToken ct = default)
    {
        return await RunWithOutputAsync(fileName, null, arguments, onOutputLine, runAsUser, runAsPassword, runAsDomain, null, ct);
    }

    public static async Task<CommandResult> RunWithOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken ct = default)
    {
        return await RunWithOutputAsync(fileName, null, arguments, onOutputLine,
            runAsUser, runAsPassword, runAsDomain, environmentVariables, ct);
    }

    private static async Task<CommandResult> RunWithOutputAsync(
        string fileName,
        string? arguments,
        IReadOnlyList<string>? argumentList,
        Action<string> onOutputLine,
        string? runAsUser,
        string? runAsPassword,
        string? runAsDomain,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var hasRunAsCredentials = !string.IsNullOrEmpty(runAsUser) && !string.IsNullOrEmpty(runAsPassword);
        if (argumentList != null)
        {
            // ProcessStartInfo.ArgumentList cannot be used together with UserName.
            // RunAs uses CreateProcessWithLogonW internally, so provide one correctly
            // quoted command-line string when launching under alternate credentials.
            if (hasRunAsCredentials)
                process.StartInfo.Arguments = CombineArgumentsForWindows(argumentList);
            else
            {
                process.StartInfo.Arguments = string.Empty;
                foreach (var argument in argumentList)
                    process.StartInfo.ArgumentList.Add(argument);
            }
        }

        if (hasRunAsCredentials)
        {
            process.StartInfo.UserName = runAsUser;
            process.StartInfo.Password = ToSecureString(runAsPassword!);
            process.StartInfo.Domain = runAsDomain ?? string.Empty;
            process.StartInfo.WorkingDirectory = Environment.SystemDirectory;
            process.StartInfo.LoadUserProfile = true;
        }

        if (environmentVariables != null)
        {
            foreach (var (name, value) in environmentVariables)
                process.StartInfo.Environment[name] = value;
        }

        try
        {
            if (!process.Start())
            {
                var message = $"进程启动失败: 未创建进程 {fileName}";
                onOutputLine(message);
                return new CommandResult(-1, string.Empty, message);
            }
        }
        catch (Exception ex)
        {
            var message = $"进程启动失败: {ex.Message}";
            onOutputLine(message);
            return new CommandResult(-1, string.Empty, message);
        }

        try { process.StandardInput.Close(); } catch { }

        using var ctr = ct.Register(() =>
        {
            try { process.Kill(true); } catch { }
        });

        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
            {
                stdout.AppendLine(line);
                onOutputLine(line);
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) != null)
            {
                stderr.AppendLine(line);
                onOutputLine(line);
            }
        }, ct);

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    internal static string CombineArgumentsForWindows(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteArgumentForWindows));

    /// <summary>
    /// Starts a visible local process whose outbound network access uses the
    /// supplied credential. This is equivalent to runas.exe /netonly, but does
    /// not expose the password on a command line or require a password prompt.
    /// </summary>
    public static CommandResult StartWithNetworkCredentials(
        string fileName,
        IReadOnlyList<string> arguments,
        string username,
        string password,
        string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(username))
            return new CommandResult(-1, string.Empty, "未提供用于远程连接的用户名。");

        var (user, domain) = ResolveNetworkCredentialIdentity(username);
        var commandLine = new StringBuilder(CombineArgumentsForWindows([fileName, .. arguments]));
        var startupInfo = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Desktop = @"winsta0\default"
        };

        try
        {
            if (!CreateProcessWithLogonW(
                    user,
                    domain,
                    password,
                    LogonNetCredentialsOnly,
                    fileName,
                    commandLine,
                    0,
                    IntPtr.Zero,
                    workingDirectory ?? Environment.SystemDirectory,
                    ref startupInfo,
                    out var processInfo))
            {
                var error = Marshal.GetLastWin32Error();
                return new CommandResult(error, string.Empty, new Win32Exception(error).Message);
            }

            CloseHandle(processInfo.ThreadHandle);
            CloseHandle(processInfo.ProcessHandle);
            return new CommandResult(0, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty, LocalStartFailurePrefix + ex.Message);
        }
    }

    internal static (string User, string? Domain) ResolveNetworkCredentialIdentity(string username)
    {
        var trimmed = username.Trim();
        if (trimmed.Contains('@'))
            return (trimmed, null);

        var (user, domain) = SplitUserDomain(trimmed);
        if (string.IsNullOrEmpty(domain))
        {
            var currentDomain = Environment.UserDomainName;
            if (!string.IsNullOrWhiteSpace(currentDomain) &&
                !currentDomain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                domain = currentDomain;
        }

        return (user, string.IsNullOrEmpty(domain) ? null : domain);
    }

    // Quote according to the Windows CommandLineToArgvW/CRT convention. This is
    // required because ProcessStartInfo.ArgumentList is not available with UserName.
    internal static string QuoteArgumentForWindows(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        var builder = new System.Text.StringBuilder(argument.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithLogonW(
        string username,
        string? domain,
        string password,
        int logonFlags,
        string applicationName,
        StringBuilder commandLine,
        int creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
