using System.Text.Json.Serialization;

namespace RemoteOpsTool.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialHealth
{
    Unverified = 0,
    Healthy,
    NeedsReauth,
    LocalLogonBlocked,
    SecretUnreadable,
    AuthorizationDenied,
    SessionConflict,
    TransportUnavailable,
}
