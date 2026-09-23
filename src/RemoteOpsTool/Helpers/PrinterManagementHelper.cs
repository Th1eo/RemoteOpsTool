namespace RemoteOpsTool.Helpers;

public static class PrinterManagementHelper
{
    /// <summary>
    /// Opens the native printer properties dialog on the operator's desktop while
    /// outbound print-spooler RPC authentication uses the selected credential.
    /// This is the print UI equivalent of runas.exe /netonly and does not create
    /// a remote process on the target host.
    /// </summary>
    public static CommandResult OpenRemoteProperties(
        string targetHost,
        string username,
        string password,
        string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return new CommandResult(-1, string.Empty, "打印机名称不能为空。");

        var plan = BuildRemotePropertiesLaunchPlan(targetHost, printerName);
        return ProcessHelper.StartWithNetworkCredentials(
            plan.FileName,
            plan.Arguments,
            username,
            password,
            Environment.SystemDirectory);
    }

    internal static PrinterPropertiesLaunchPlan BuildRemotePropertiesLaunchPlan(
        string targetHost,
        string printerName)
    {
        var normalizedHost = HostHelper.NormalizeHost(targetHost);
        var rundll32Path = Path.Combine(Environment.SystemDirectory, "rundll32.exe");
        return new PrinterPropertiesLaunchPlan(
            rundll32Path,
            ["printui.dll,PrintUIEntry", "/p", "/n", printerName, $@"/c\\{normalizedHost}"],
            normalizedHost);
    }

    /// <summary>
    /// Opens the Windows "Add Printer" wizard on the operator's desktop and points
    /// it at the target host's print spooler through <c>/c</c>. The wizard runs under
    /// the selected credential with a network-only logon (equivalent to
    /// runas.exe /netonly), so no process is created on the target host and the
    /// window never appears on the target user's desktop.
    /// </summary>
    public static CommandResult OpenRemoteAddWizard(
        string targetHost,
        string username,
        string password)
    {
        if (HostHelper.IsLocalHost(targetHost))
            return new CommandResult(-1, string.Empty, "目标主机为本机，请使用本机添加打印机向导。");

        var plan = BuildRemoteAddWizardLaunchPlan(targetHost);
        return ProcessHelper.StartWithNetworkCredentials(
            plan.FileName,
            plan.Arguments,
            username,
            password,
            Environment.SystemDirectory);
    }

    internal static PrinterAddWizardLaunchPlan BuildRemoteAddWizardLaunchPlan(string targetHost)
    {
        var normalizedHost = HostHelper.NormalizeHost(targetHost);
        var printuiPath = Path.Combine(Environment.SystemDirectory, "printui.exe");
        return new PrinterAddWizardLaunchPlan(
            printuiPath,
            ["/il", $@"/c\\{normalizedHost}"],
            normalizedHost);
    }

    /// <summary>
    /// Compares printer snapshots taken before and after the add-printer wizard ran.
    /// The <c>/c</c> switch is honored by the install switches (/il, /if, /ia, /id,
    /// /ii), but some wizard branches (for example "add a local printer") can still create
    /// the queue on the management machine. Callers use this to detect that case and
    /// report it instead of claiming a silent success.
    /// </summary>
    internal static PrinterAddWizardOutcome EvaluateAddWizardOutcome(
        IEnumerable<string>? targetPrintersBefore,
        IEnumerable<string>? targetPrintersAfter,
        IEnumerable<string>? localPrintersBefore,
        IEnumerable<string>? localPrintersAfter,
        out IReadOnlyList<string> addedTargetPrinters,
        out IReadOnlyList<string> addedLocalPrinters)
    {
        addedTargetPrinters = DiffPrinterNames(targetPrintersBefore, targetPrintersAfter);
        addedLocalPrinters = DiffPrinterNames(localPrintersBefore, localPrintersAfter);

        if (addedTargetPrinters.Count > 0)
            return PrinterAddWizardOutcome.TargetUpdated;

        if (addedLocalPrinters.Count > 0)
            return PrinterAddWizardOutcome.InstalledOnLocalMachine;

        return PrinterAddWizardOutcome.NoChange;
    }

    private static List<string> DiffPrinterNames(IEnumerable<string>? before, IEnumerable<string>? after)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in before ?? [])
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                seen.Add(trimmed);
        }

        var added = new List<string>();
        foreach (var name in after ?? [])
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (seen.Add(trimmed))
                added.Add(trimmed);
        }

        return added;
    }
}

internal sealed record PrinterPropertiesLaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string TargetHost);

internal sealed record PrinterAddWizardLaunchPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    string TargetHost);

internal enum PrinterAddWizardOutcome
{
    NoChange,
    TargetUpdated,
    InstalledOnLocalMachine
}
