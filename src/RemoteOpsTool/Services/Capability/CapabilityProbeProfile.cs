namespace RemoteOpsTool.Services.Capability;

/// <summary>
/// Selects the minimum capability probe set needed by an operation. Keeping
/// profiles separate prevents read-only WMI queries from paying for a PsExec
/// launch, session query, or scheduled-task probe they can never use.
/// </summary>
public enum CapabilityProbeProfile
{
    /// <summary>Full diagnostic probe used by connection tests and explicit refresh.</summary>
    Full = 0,

    /// <summary>WMI inventory only; PsExec is probed lazily after a transport failure.</summary>
    InventoryWmiOnly = 1,

    /// <summary>WMI registry access only; command fallback is probed lazily.</summary>
    RegistryWmiOnly = 2,

    /// <summary>WMI first, PsExec fallback already known before execution.</summary>
    Command = 3,

    /// <summary>WMI-only warm probe for ordinary commands; PsExec is lazy.</summary>
    CommandWmiFirst = 4,

    /// <summary>WMI and user-session probing only.</summary>
    SessionQuery = 5,

    /// <summary>No preflight required; interactive launch owns its fallback chain.</summary>
    InteractiveLaunch = 6,
}
