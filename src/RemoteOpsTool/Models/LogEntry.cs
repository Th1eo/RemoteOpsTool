namespace RemoteOpsTool.Models;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

public record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public string DisplayText => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
}
