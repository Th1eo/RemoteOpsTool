namespace RemoteOpsTool.Helpers;

public static class CredentialMasker
{
    public static string MaskPasswordInCommand(string commandLine, string? password)
    {
        if (string.IsNullOrEmpty(password)) return commandLine;
        return commandLine.Replace(password, "********");
    }
}
