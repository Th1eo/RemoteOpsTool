using System.Text.RegularExpressions;

namespace RemoteOpsTool.Helpers;

public static class CredentialMasker
{
    private static readonly Regex PasswordAssignmentRegex = new(
        @"(?<prefix>\bpassword\s*=\s*)(?:""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string MaskPasswordInCommand(string commandLine, string? password)
    {
        if (string.IsNullOrEmpty(commandLine))
            return commandLine;

        var masked = PasswordAssignmentRegex.Replace(
            commandLine,
            match => $"{match.Groups["prefix"].Value}\"********\"");

        return string.IsNullOrEmpty(password)
            ? masked
            : masked.Replace(password, "********", StringComparison.Ordinal);
    }
}
