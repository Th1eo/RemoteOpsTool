namespace RemoteOpsTool.Services.Transports;

/// <summary>
/// Coarse command classes used by route learning. The shape is deliberately
/// based on properties the router already knows, so it never records command
/// text, script contents, or credentials.
/// </summary>
public enum RemoteCommandShape
{
    ShortCommand = 0,
    LongCommand = 1,
    Script = 2,
    InteractiveLaunch = 3,
}

/// <summary>Classifies a command without retaining or logging its payload.</summary>
internal static class RemoteCommandShapeClassifier
{
    // PsExec's RunAs launcher has a Windows command-line budget. Keep this
    // threshold below that limit so long payloads are learned separately.
    internal const int LongCommandThreshold = 1_024;

    public static RemoteCommandShape Classify(
        RemoteOperationKind operation,
        RemoteCommand command)
    {
        if (operation == RemoteOperationKind.InteractiveLaunch || command.InteractiveSession)
            return RemoteCommandShape.InteractiveLaunch;

        if (command.PreferPsExec)
            return RemoteCommandShape.Script;

        return command.Command.Length >= LongCommandThreshold
            ? RemoteCommandShape.LongCommand
            : RemoteCommandShape.ShortCommand;
    }
}
