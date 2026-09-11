namespace RemoteOpsTool.Helpers;

public readonly record struct ActiveSessionInfo(int SessionId, string Username)
{
    public static ActiveSessionInfo None => new(-1, string.Empty);
    public bool IsValid => SessionId > 0;
}

public static class SessionHelper
{
    private static readonly string[] ActiveStates = ["Active", "活动"];
    private static readonly string[] DisconnectedStates = ["Disc", "Disconnected", "断开"];
    private static readonly string[] SessionNameTokens = ["console", "services", "rdp-tcp", "rdp-tcp#"];

    public static int ParseSessionId(string output) => ParseActiveSession(output).SessionId;

    public static ActiveSessionInfo ParseActiveSession(string output, int? preferredSessionId = null)
    {
        var activeSessions = new List<ActiveSessionInfo>();
        ActiveSessionInfo consoleFallback = ActiveSessionInfo.None;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeToken)
                .Where(part => part.Length > 0)
                .ToArray();
            if (parts.Length == 0)
                continue;

            // `query session` and `query user` place the numeric session ID
            // immediately before the state column. Capture the username as well so
            // a scheduled-task fallback can run in the desktop user's session rather
            // than assuming that the remote administration credential is logged on.
            var activeIndex = Array.FindIndex(parts, IsActiveState);
            if (activeIndex > 0)
            {
                for (var idIndex = activeIndex - 1; idIndex >= 0; idIndex--)
                {
                    if (!int.TryParse(parts[idIndex], out var id) || id < 0)
                        continue;

                    var username = FindUsername(parts, idIndex);
                    activeSessions.Add(new ActiveSessionInfo(id, username));
                    break;
                }
            }

            // Localized or redirected output can contain an unfamiliar state word.
            // Keep a non-disconnected console session as a final fallback.
            if (parts.Any(part => part.Equals("console", StringComparison.OrdinalIgnoreCase)) &&
                !parts.Any(IsDisconnectedState))
            {
                var idIndex = Array.FindIndex(parts, part => int.TryParse(part, out var value) && value >= 0);
                if (idIndex >= 0 && int.TryParse(parts[idIndex], out var id))
                    consoleFallback = new ActiveSessionInfo(id, FindUsername(parts, idIndex));
            }
        }

        if (preferredSessionId is int preferred)
        {
            var preferredSession = activeSessions.FirstOrDefault(session => session.SessionId == preferred);
            if (preferredSession.IsValid)
                return preferredSession;
            return consoleFallback.SessionId == preferred ? consoleFallback : ActiveSessionInfo.None;
        }

        return activeSessions.Count > 0 ? activeSessions[0] : consoleFallback;
    }

    private static string FindUsername(IReadOnlyList<string> parts, int idIndex)
    {
        if (idIndex <= 0)
            return string.Empty;

        var candidate = parts[idIndex - 1];
        if (IsSessionName(candidate) && idIndex > 1)
            candidate = parts[idIndex - 2];

        if (int.TryParse(candidate, out _) || IsState(candidate) || IsSessionName(candidate))
            return string.Empty;
        return candidate;
    }

    private static string NormalizeToken(string token) =>
        token.Trim().Trim('>', '*', '\\', ':', ';', ',');

    private static bool IsActiveState(string token) =>
        ActiveStates.Any(state => token.Equals(state, StringComparison.OrdinalIgnoreCase));

    private static bool IsDisconnectedState(string token) =>
        DisconnectedStates.Any(state => token.Equals(state, StringComparison.OrdinalIgnoreCase));

    private static bool IsState(string token) => IsActiveState(token) || IsDisconnectedState(token) ||
        token.Equals("Listen", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("侦听", StringComparison.OrdinalIgnoreCase);

    private static bool IsSessionName(string token) =>
        SessionNameTokens.Any(name => token.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            (name.EndsWith('#') && token.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
}
