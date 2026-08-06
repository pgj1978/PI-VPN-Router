using System.ComponentModel.DataAnnotations;

namespace PiRouter.Api.Contracts;

// Typed contracts throughout. The previous API returned Task<object> holding anonymous
// types, so there was no schema, no OpenAPI worth generating, and the client had to guess
// at field names — which is how the UI ended up calling an endpoint that did not exist.

public sealed record VpnStatusResponse(
    bool Connected,
    string? Profile,
    string InterfaceName,
    string? Endpoint,
    double? HandshakeAgeSeconds,
    long BytesReceived,
    long BytesSent,
    bool Healthy);

public sealed record VpnProfileResponse(
    string Name,
    string? Endpoint,
    string? Dns,
    int? Mtu,
    bool Active);

public sealed record VpnProfilesResponse(
    IReadOnlyList<VpnProfileResponse> Profiles,
    bool KillSwitchEnabled,
    string? ActiveProfile);

public sealed record VpnOperationResponse(bool Success, string? Error, string Log);

public sealed record AddVpnProfileRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required, StringLength(8192, MinimumLength = 1)]
    public required string Config { get; init; }
}

public sealed record KillSwitchRequest
{
    public required bool Enabled { get; init; }
}

public sealed record DeviceResponse(
    string Mac,
    string? Ip,
    string? Hostname,
    string? Label,
    bool BypassVpn,
    string? StaticIp,
    bool Online,
    DateTimeOffset? LeaseExpires);

public sealed record DevicesResponse(IReadOnlyList<DeviceResponse> Devices);

public sealed record SetBypassRequest
{
    public required bool Bypass { get; init; }
}

public sealed record SetStaticIpRequest
{
    /// <summary>Null or empty removes the reservation.</summary>
    public string? Ip { get; init; }
}

public sealed record SetLabelRequest
{
    [StringLength(64)]
    public string? Label { get; init; }
}

public sealed record DomainResponse(
    string Domain,
    bool Enabled,
    IReadOnlyList<string> ResolvedIps,
    DateTimeOffset? LastResolvedAt);

public sealed record DomainsResponse(IReadOnlyList<DomainResponse> Domains);

public sealed record AddDomainRequest
{
    [Required, StringLength(253, MinimumLength = 1)]
    public required string Domain { get; init; }
}

public sealed record DhcpResponse(bool Enabled, string RangeStart, string RangeEnd, string LeaseTime);

public sealed record SetDhcpRequest
{
    public required bool Enabled { get; init; }
    public string? RangeStart { get; init; }
    public string? RangeEnd { get; init; }
    public string? LeaseTime { get; init; }
}

public sealed record SystemInfoResponse(
    string LanInterface,
    string WanInterface,
    string VpnInterface,
    string? LanAddress,
    string? WanAddress,
    string? WanGateway,
    bool IpForwarding,
    bool DnsmasqRunning,
    string Version,
    TimeSpan Uptime);

public sealed record RulePreviewResponse(
    string Fingerprint,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unexpected,
    IReadOnlyList<string> MissingChains,
    bool InSync,
    DateTimeOffset? LastReconciledAt);

public sealed record DiagnosticCheckResponse(
    string Id,
    string Name,
    string Status,
    string Detail,
    string? Remediation);

public sealed record DiagnosticsResponse(
    DateTimeOffset RanAt,
    string Overall,
    IReadOnlyList<DiagnosticCheckResponse> Checks);

public sealed record LogEntryResponse(
    long Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception);

public sealed record LogsResponse(IReadOnlyList<LogEntryResponse> Entries);

public sealed record ErrorResponse(string Error);
