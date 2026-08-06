using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Firewall;
using PiRouter.Core.Net;

namespace PiRouter.Core.Services;

public interface IStateBuilder
{
    Task<RouterState> BuildAsync(CancellationToken ct = default);
}

/// <summary>
/// Merges persisted intent with live facts to produce the <see cref="RouterState"/> that
/// gets compiled into rules.
///
/// This is where MAC-to-IP resolution happens, on every single build. That is the whole
/// mechanism by which bypass now survives a DHCP lease change: nothing anywhere caches a
/// device's address, so a new lease is picked up on the next reconcile tick automatically.
/// </summary>
public sealed class StateBuilder(
    IConfigStore config,
    ILeaseReader leases,
    IVpnService vpn,
    INetworkDiscovery discovery,
    IDomainResolver resolver,
    IOptions<RouterOptions> options,
    ILogger<StateBuilder> logger) : IStateBuilder
{
    private readonly RouterOptions _options = options.Value;

    public async Task<RouterState> BuildAsync(CancellationToken ct = default)
    {
        var current = config.Current;
        var status = await vpn.GetStatusAsync(ct);

        // Prefer the address the interface actually has over the configured one, so a
        // manually changed LAN address does not silently invalidate every rule.
        var liveLanAddress = await discovery.GetInterfaceAddressAsync(_options.LanInterface, ct);
        var lanAddress = liveLanAddress ?? _options.LanAddress;
        if (liveLanAddress is not null && liveLanAddress != _options.LanAddress)
            logger.LogDebug("Using live LAN address {Live} (configured: {Configured})", liveLanAddress, _options.LanAddress);

        var gateway = await discovery.GetDefaultGatewayAsync(_options.WanInterface, ct);

        var deviceIps = await ResolveBypassDevicesAsync(ct);
        var domainIps = await ResolveBypassDomainsAsync(ct);

        return new RouterState
        {
            LanInterface = _options.LanInterface,
            WanInterface = _options.WanInterface,
            VpnInterface = _options.VpnInterface,
            LanIp = Cidr.AddressOf(lanAddress),
            LanNetwork = Cidr.NetworkOf(lanAddress),
            WanGateway = gateway,
            VpnUp = status.Up,
            VpnTableId = status.TableId,
            KillSwitchEnabled = current.KillSwitchEnabled,
            BypassMark = _options.BypassMark,
            BypassRulePriority = _options.BypassRulePriority,
            VpnRulePriority = _options.VpnRulePriority,
            VpnMss = _options.VpnMss,
            BypassDeviceIps = deviceIps,
            BypassDomainIps = domainIps,
            TunnelExcludedPrefixes = _options.TunnelExcludedPrefixes,
        };
    }

    private async Task<List<string>> ResolveBypassDevicesAsync(CancellationToken ct)
    {
        var wanted = config.Current.Devices.Where(d => d.BypassVpn).ToList();
        if (wanted.Count == 0) return [];

        var currentLeases = await leases.ReadAsync(ct);
        var ips = new List<string>();

        foreach (var device in wanted)
        {
            var mac = device.Mac.Trim().ToLowerInvariant();

            // A reservation is authoritative even before the device next renews its lease.
            var ip = device.StaticIp is { Length: > 0 } reserved && Cidr.IsValidIpv4(reserved)
                ? reserved
                : currentLeases.FirstOrDefault(l => l.Mac == mac)?.Ip;

            if (ip is null)
            {
                logger.LogDebug("Device {Mac} is marked for bypass but holds no current lease; skipping until it appears", mac);
                continue;
            }

            ips.Add(ip);
        }
        return ips;
    }

    private async Task<List<string>> ResolveBypassDomainsAsync(CancellationToken ct)
    {
        var ips = new List<string>();
        foreach (var domain in config.Current.DomainBypasses.Where(d => d.Enabled))
            ips.AddRange(await resolver.ResolveAsync(domain.Domain, ct));

        return ips;
    }
}
