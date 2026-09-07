namespace RemoteOpsTool.Helpers;

public static class SessionHelper
{
    private static readonly string[] ActiveStates = ["Active", "活动"];
    private static readonly string[] DisconnectedStates = ["Disc", "Disconnected", "断开"];

    public static int ParseSessionId(string output)
    {
        int consoleFallbackId = -1;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeToken)
                .Where(part => part.Length > 0)
                .ToArray();
            if (parts.Length == 0)
                continue;

            // `query session` and `query user` both place the numeric session ID
            // immediately before the state column. Prefer an Active session so
            // interactive programs are sent to the desktop the operator can see.
            var activeIndex = Array.FindIndex(parts, IsActiveState);
            if (activeIndex > 0)
            {
                for (var i = activeIndex - 1; i >= 0; i--)
                {
                    if (int.TryParse(parts[i], out var id) && id >= 0)
                        return id;
                }
            }

            // Some redirected query.exe output appends punctuation/backslashes, and
            // localized systems may expose an unfamiliar state word. Keep a console
            // session only as a final fallback, but never select a known disconnected
            // console session.
            if (parts.Any(part => part.Equals("console", StringComparison.OrdinalIgnoreCase)) &&
                !parts.Any(IsDisconnectedState))
            {
                var id = parts.Select(part => int.TryParse(part, out var value) ? value : -1)
                    .FirstOrDefault(value => value >= 0, -1);
                if (id >= 0)
                    consoleFallbackId = id;
            }
        }

        return consoleFallbackId;
    }

    private static string NormalizeToken(string token) =>
        token.Trim().Trim('>', '*', '\\', ':', ';', ',');

    private static bool IsActiveState(string token) =>
        ActiveStates.Any(state => token.Equals(state, StringComparison.OrdinalIgnoreCase));

    private static bool IsDisconnectedState(string token) =>
        DisconnectedStates.Any(state => token.Equals(state, StringComparison.OrdinalIgnoreCase));
}
