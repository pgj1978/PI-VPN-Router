using Microsoft.AspNetCore.Mvc;
using PiRouter.Api.Contracts;
using PiRouter.Core.Models;
using PiRouter.Core.Net;
using PiRouter.Core.Services;

namespace PiRouter.Api.Controllers;

[ApiController]
[Route("api/devices")]
[Produces("application/json")]
public sealed class DevicesController(
    IConfigStore config,
    ILeaseReader leases,
    IDnsmasqService dnsmasq,
    IReconciler reconciler,
    ILogger<DevicesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DevicesResponse>> List(CancellationToken ct)
    {
        var currentLeases = await leases.ReadAsync(ct);
        var configured = config.Current.Devices;

        var devices = new List<DeviceResponse>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lease in currentLeases)
        {
            var saved = Find(configured, lease.Mac);
            seen.Add(lease.Mac);

            devices.Add(new DeviceResponse(
                Mac: lease.Mac,
                Ip: lease.Ip,
                Hostname: lease.Hostname,
                Label: saved?.Label,
                BypassVpn: saved?.BypassVpn ?? false,
                StaticIp: saved?.StaticIp,
                Online: true,
                LeaseExpires: lease.Expires));
        }

        // Devices we hold settings for but which are not currently on the network. Showing
        // them means a bypass toggle does not vanish from the UI when a laptop sleeps.
        foreach (var saved in configured.Where(d => !seen.Contains(d.Mac)))
        {
            devices.Add(new DeviceResponse(
                Mac: saved.Mac,
                Ip: saved.StaticIp,
                Hostname: null,
                Label: saved.Label,
                BypassVpn: saved.BypassVpn,
                StaticIp: saved.StaticIp,
                Online: false,
                LeaseExpires: null));
        }

        return Ok(new DevicesResponse(
            [.. devices.OrderByDescending(d => d.Online).ThenBy(d => d.Label ?? d.Hostname ?? d.Mac, StringComparer.OrdinalIgnoreCase)]));
    }

    [HttpPut("{mac}/bypass")]
    public async Task<ActionResult<DevicesResponse>> SetBypass(
        string mac, [FromBody] SetBypassRequest request, CancellationToken ct)
    {
        if (!MacAddress.TryNormalise(mac, out var normalised))
            return BadRequest(new ErrorResponse($"'{mac}' is not a MAC address"));

        await config.MutateAsync(c => Upsert(c, normalised).BypassVpn = request.Bypass, ct);

        // Reconcile inline so the caller's next status read already reflects reality.
        await reconciler.ReconcileNowAsync(ct);
        logger.LogInformation("Device {Mac} bypass {State}", normalised, request.Bypass ? "ON" : "OFF");

        return await List(ct);
    }

    [HttpPut("{mac}/static-ip")]
    public async Task<ActionResult<DevicesResponse>> SetStaticIp(
        string mac, [FromBody] SetStaticIpRequest request, CancellationToken ct)
    {
        if (!MacAddress.TryNormalise(mac, out var normalised))
            return BadRequest(new ErrorResponse($"'{mac}' is not a MAC address"));

        var ip = string.IsNullOrWhiteSpace(request.Ip) ? null : request.Ip.Trim();
        if (ip is not null && !Cidr.IsValidIpv4(ip))
            return BadRequest(new ErrorResponse($"'{ip}' is not a valid IPv4 address"));

        await config.MutateAsync(c => Upsert(c, normalised).StaticIp = ip, ct);

        // A reservation change only needs dnsmasq to re-read its hosts directory, which is a
        // SIGHUP. No restart, so DHCP is never interrupted for other devices.
        await dnsmasq.WriteStaticLeasesAsync(config.Current.Devices, ct);
        await reconciler.ReconcileNowAsync(ct);

        logger.LogInformation("Device {Mac} reservation set to {Ip}", normalised, ip ?? "(none)");
        return await List(ct);
    }

    [HttpPut("{mac}/label")]
    public async Task<ActionResult<DevicesResponse>> SetLabel(
        string mac, [FromBody] SetLabelRequest request, CancellationToken ct)
    {
        if (!MacAddress.TryNormalise(mac, out var normalised))
            return BadRequest(new ErrorResponse($"'{mac}' is not a MAC address"));

        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        await config.MutateAsync(c => Upsert(c, normalised).Label = label, ct);

        return await List(ct);
    }

    [HttpDelete("{mac}")]
    public async Task<ActionResult<DevicesResponse>> Forget(string mac, CancellationToken ct)
    {
        if (!MacAddress.TryNormalise(mac, out var normalised))
            return BadRequest(new ErrorResponse($"'{mac}' is not a MAC address"));

        await config.MutateAsync(c => c.Devices.RemoveAll(d =>
            d.Mac.Equals(normalised, StringComparison.OrdinalIgnoreCase)), ct);

        await dnsmasq.WriteStaticLeasesAsync(config.Current.Devices, ct);
        await reconciler.ReconcileNowAsync(ct);

        return await List(ct);
    }

    private static DeviceConfig Find(IEnumerable<DeviceConfig> devices, string mac) =>
        devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))!;

    private static DeviceConfig Upsert(RouterConfig config, string mac)
    {
        var existing = config.Devices.FirstOrDefault(d => d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var created = new DeviceConfig { Mac = mac };
        config.Devices.Add(created);
        return created;
    }

}
