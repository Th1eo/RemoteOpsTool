using System.Text.RegularExpressions;

namespace RemoteOpsTool.Helpers;

public static class CredentialMasker
{
    private static readonly Regex PasswordAssignmentRegex = new(
        @"(?<prefix>\bpassword\s*=\s*)(?:""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Matches the value that follows a password switch such as "-p secret",
    // "-p:secret", "/p secret" or "-p=secret". The prefix lookbehind keeps
    // switches like "-Path" or "/profile" from being mistaken for "-p".
    private static readonly Regex PasswordSwitchRegex = new(
        @"(?<prefix>(?<![-\w/])[-\/][Pp])(?<separator>\s*[:=]\s*|\s+)(?:""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string MaskPasswordInCommand(string commandLine, string? password)
    {
        if (string.IsNullOrEmpty(commandLine))
            return commandLine;

        var masked = PasswordAssignmentRegex.Replace(
            commandLine,
            match => $"{match.Groups["prefix"].Value}\"********\"");

        masked = PasswordSwitchRegex.Replace(
            masked,
            match => $"{match.Groups["prefix"].Value}{match.Groups["separator"].Value}********");

        return string.IsNullOrEmpty(password)
            ? masked
            : masked.Replace(password, "********", StringComparison.Ordinal);
    }

    /// <summary>
    /// Masks password values inside an already tokenized argument list. Masking
    /// must happen before any escaping/quoting is applied for logging, because
    /// escaping backslashes or quotes changes the literal text and defeats
    /// string-based replacement.
    /// </summary>
    public static IReadOnlyList<string> MaskPasswordArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var masked = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i] ?? string.Empty;
            if (TryMaskInlinePasswordSwitch(argument, out var inlineMasked))
            {
                masked[i] = inlineMasked;
                continue;
            }

            masked[i] = argument;
            if (IsStandalonePasswordSwitch(argument) && i + 1 < arguments.Count)
            {
                masked[i + 1] = "********";
                i++;
            }
        }

        return masked;
    }

    private static bool IsStandalonePasswordSwitch(string argument) =>
        argument.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("/p", StringComparison.OrdinalIgnoreCase);

    private static bool TryMaskInlinePasswordSwitch(string argument, out string masked)
    {
        masked = argument;

        if (argument.Length <= 2)
            return false;

        var isSlashSwitch = argument[0] == '-' || argument[0] == '/';
        if (!isSlashSwitch || (argument[1] != 'p' && argument[1] != 'P'))
            return false;

        var separator = argument[2];
        if (separator != ':' && separator != '=')
            return false;

        masked = string.Concat(argument.AsSpan(0, 3), "********");
        return true;
    }
}
