namespace RemoteOpsTool.Helpers;

public static class SessionHelper
{
    public static int ParseSessionId(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("console", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
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
