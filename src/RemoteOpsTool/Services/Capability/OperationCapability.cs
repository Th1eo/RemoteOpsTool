using RemoteOpsTool.Models;
using RemoteOpsTool.Services.Transports;

namespace RemoteOpsTool.Services.Capability;

/// <summary>Health of one transport for one operation class.</summary>
public enum CapabilityHealth
{
    Unknown = 0,
    Available = 1,
    TransientFailure = 2,
    PermissionDenied = 3,
    HardUnavailable = 4,
}

/// <summary>
/// Reason a transport/operation pair is currently considered unhealthy.
/// Kept separate from <see cref="CapabilityHealth"/> so routing can apply a
/// different TTL to a timeout, an access-denied result and a disabled service.
/// </summary>
public enum CapabilityFailureKind
{
    None = 0,
    Timeout = 1,
    Network = 2,
    Authentication = 3,
    PermissionDenied = 4,
    Disabled = 5,
    NotFound = 6,
    Protocol = 7,
    RemoteCommand = 8,
    Unknown = 9,
}

/// <summary>Operation-specific cache key. A command failure for PsExec must not suppress the interactive PsExec path.</summary>
public readonly record struct OperationCapabilityKey(
    RemoteOperationKind Operation,
    RemoteTransportKind Transport);

/// <summary>Point-in-time operation-specific capability record.</summary>
public sealed record OperationCapability(
    RemoteOperationKind Operation,
    RemoteTransportKind Transport,
    CapabilityHealth Health,
    CapabilityFailureKind FailureKind,
    string Detail,
    DateTimeOffset CheckedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    public bool IsRouteReady(DateTimeOffset now) =>
        !IsExpired(now) &&
        Health is CapabilityHealth.Unknown or CapabilityHealth.Available;

    public static OperationCapability Unknown(OperationCapabilityKey key, DateTimeOffset now) =>
        new(key.Operation, key.Transport, CapabilityHealth.Unknown,
            CapabilityFailureKind.Unknown, string.Empty, now, now);
}

/// <summary>Classifies failures and assigns operation-specific TTLs.</summary>
internal static class CapabilityPolicy
{
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TransientFailureTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PermissionFailureTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HardFailureTtl = TimeSpan.FromMinutes(30);

    public static OperationCapability CreateAvailable(
        RemoteOperationKind operation,
        RemoteTransportKind transport,
        DateTimeOffset checkedAt,
        string detail = "")
    {
        return new OperationCapability(
            operation,
            transport,
            CapabilityHealth.Available,
            CapabilityFailureKind.None,
            detail,
            checkedAt,
            checkedAt + SuccessTtl);
    }

    public static OperationCapability CreateFailure(
        RemoteOperationKind operation,
        RemoteTransportKind transport,
        CapabilityFailureKind failureKind,
        string detail,
        DateTimeOffset checkedAt,
        TimeSpan? ttl = null)
    {
        var (health, defaultTtl) = failureKind switch
        {
            CapabilityFailureKind.PermissionDenied or
            CapabilityFailureKind.Authentication =>
                (CapabilityHealth.PermissionDenied, PermissionFailureTtl),
            CapabilityFailureKind.Disabled or
            CapabilityFailureKind.NotFound or
            CapabilityFailureKind.RemoteCommand =>
                (CapabilityHealth.HardUnavailable, HardFailureTtl),
            _ => (CapabilityHealth.TransientFailure, TransientFailureTtl),
        };

        return new OperationCapability(
            operation,
            transport,
            health,
            failureKind,
            detail,
            checkedAt,
            checkedAt + (ttl ?? defaultTtl));
    }

    public static CapabilityFailureKind ClassifyResult(CommandResult result)
    {
        if (result.Success)
            return CapabilityFailureKind.None;

        return ClassifyText($"{result.StdOut}\n{result.StdErr}");
    }

    public static CapabilityFailureKind ClassifyProbeFailure(string detail) =>
        ClassifyText(detail);

    private static CapabilityFailureKind ClassifyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CapabilityFailureKind.Unknown;

        if (ContainsAny(text, "timeout", "timed out", "超时", "等待超时"))
            return CapabilityFailureKind.Timeout;

        if (ContainsAny(text, "access is denied", "access denied", "权限", "拒绝访问", "访问被拒绝", "0x80070005"))
            return CapabilityFailureKind.PermissionDenied;

        if (ContainsAny(text, "logon failure", "login failure", "用户名或密码", "身份验证", "认证失败", "0x8007052e", "1326"))
            return CapabilityFailureKind.Authentication;

        if (ContainsAny(text, "disabled", "not started", "未启用", "未启动", "已禁用", "1062", "1058"))
            return CapabilityFailureKind.Disabled;

        if (ContainsAny(text, "not found", "找不到", "不存在", "failed to find", "无法找到"))
            return CapabilityFailureKind.NotFound;

        if (ContainsAny(text, "rpc server", "network path", "network unavailable", "网络", "远程过程调用", "0x800706ba", "0x80072ee2"))
            return CapabilityFailureKind.Network;

        return CapabilityFailureKind.Protocol;
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
