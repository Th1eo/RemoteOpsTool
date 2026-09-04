namespace RemoteOpsTool.Helpers;

public static class SessionHelper
{
    public static int ParseSessionId(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            // `query session` and `query user` both place the numeric session ID
            // immediately before the state column. Prefer an Active session so
            // interactive programs are sent to the desktop the operator can see.
            var activeIndex = Array.FindIndex(parts,
                part => part.Equals("Active", StringComparison.OrdinalIgnoreCase));
            if (activeIndex > 0)
            {
                for (var i = activeIndex - 1; i >= 0; i--)
                {
                    if (int.TryParse(parts[i], out var id) && id >= 0)
                        return id;
                }
            }

            // Preserve the previous console-session fallback for systems whose
            // output is localized or does not expose an explicit Active state.
            if (line.Contains("console", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in parts)
                {
                    if (int.TryParse(part, out var id) && id >= 0)
                        return id;
                }
            }
        }
        return -1;
    }
}
