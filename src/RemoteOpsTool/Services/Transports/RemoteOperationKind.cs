namespace RemoteOpsTool.Services.Transports;

/// <summary>Operation classes the router routes independently.</summary>
public enum RemoteOperationKind
{
    /// <summary>Ordinary command whose output can be redirected.</summary>
    Command = 0,

    /// <summary>Launch a GUI program on the target's interactive desktop.</summary>
    InteractiveLaunch = 1,

    /// <summary>Enumerate processes, sessions, services, software, etc.</summary>
    Inventory = 2,

    RegistryRead = 3,
    RegistryWrite = 4,
}
