using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Firewall;
using PiRouter.Core.Logging;
using PiRouter.Core.Net;
using PiRouter.Core.Process;
using PiRouter.Core.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration
// Everything topology-related comes from here. ROUTER_* environment variables (supplied by
// deploy/.env) override appsettings.json. No network detail is hardcoded anywhere else.
builder.Configuration.AddEnvironmentVariables(prefix: "ROUTER_");
builder.Services.Configure<RouterOptions>(builder.Configuration);

// ---------------------------------------------------------------- logging
var logBuffer = new LogBuffer(capacity: 5000);
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new LogBufferProvider(logBuffer));
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// ---------------------------------------------------------------- services
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IRuleApplier, RuleApplier>();
builder.Services.AddSingleton<IConfigStore, ConfigStore>();
builder.Services.AddSingleton<ILeaseReader, LeaseReader>();
builder.Services.AddSingleton<INetworkDiscovery, NetworkDiscovery>();
builder.Services.AddSingleton<IDomainResolver, DomainResolver>();
builder.Services.AddSingleton<IStateBuilder, StateBuilder>();
builder.Services.AddSingleton<IVpnService, VpnService>();
builder.Services.AddSingleton<IDockerClient, DockerClient>();
builder.Services.AddSingleton<IDnsmasqService, DnsmasqService>();
builder.Services.AddSingleton<IDiagnosticsService, DiagnosticsService>();

// The reconciler is both a hosted service and something controllers call directly, so it is
// registered once and surfaced under both.
builder.Services.AddSingleton<ReconcilerService>();
builder.Services.AddSingleton<IReconciler>(sp => sp.GetRequiredService<ReconcilerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReconcilerService>());
builder.Services.AddHostedService<VpnWatchdogService>();

builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// The UI runs on a different origin during development (ng serve on :4200). In production
// nginx proxies /api same-origin, so this policy only matters for dev.
builder.Services.AddCors(o => o.AddPolicy("ui", p => p
    .SetIsOriginAllowed(_ => true)
    .AllowAnyMethod()
    .AllowAnyHeader()));

// ---------------------------------------------------------------- listen addresses
// Loopback plus the LAN address only. The previous build listened on 0.0.0.0, which exposed
// an unauthenticated API that can reboot the Pi and rewrite firewall rules on the upstream
// WAN side too, not just the LAN.
var startupOptions = builder.Configuration.Get<RouterOptions>() ?? new RouterOptions();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    var port = startupOptions.ApiPort;
    kestrel.ListenLocalhost(port);

    if (startupOptions.LoopbackOnly) return;

    if (IPAddress.TryParse(Cidr.AddressOf(startupOptions.LanAddress), out var lan))
    {
        // Binding the LAN address is best-effort: on a dev machine that address belongs to
        // no local interface, and that must not stop the API from starting on loopback.
        try
        {
            kestrel.Listen(lan, port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not bind {lan}:{port} ({ex.Message}); listening on loopback only.");
        }
    }
});

var app = builder.Build();

var log = app.Services.GetRequiredService<ILogger<Program>>();
var options = app.Services.GetRequiredService<IOptions<RouterOptions>>().Value;

log.LogInformation(
    "PiRouter starting. LAN {Lan} on {LanIf}, WAN {WanIf}, VPN {VpnIf}. Rule application {Mode}.",
    options.LanAddress, options.LanInterface, options.WanInterface, options.VpnInterface,
    options.ApplyRules ? "enabled" : "OBSERVE-ONLY");

// Say so loudly at startup if the configured interfaces do not exist, rather than emitting
// a stream of confusing rule failures later on.
using (var scope = app.Services.CreateScope())
{
    var discovery = scope.ServiceProvider.GetRequiredService<INetworkDiscovery>();

    foreach (var (iface, role, key) in new[]
             {
                 (options.LanInterface, "LAN", "ROUTER_LanInterface"),
                 (options.WanInterface, "WAN", "ROUTER_WanInterface"),
             })
    {
        if (!await discovery.InterfaceExistsAsync(iface))
            log.LogError("{Role} interface '{Interface}' does not exist. Set {Key} in deploy/.env.",
                role, iface, key);
    }

    // Make sure dnsmasq has a usable config. On a fresh stack it starts with neither
    // upstream servers nor a DHCP range, so without this it silently serves nothing until
    // somebody happens to open the DHCP settings page.
    try
    {
        var dnsmasq = scope.ServiceProvider.GetRequiredService<IDnsmasqService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfigStore>();
        await dnsmasq.EnsureConfiguredAsync(config.Current.Devices);
    }
    catch (Exception ex)
    {
        // Never fatal: the router must still come up and route even if DHCP setup fails.
        log.LogError(ex, "Could not ensure the dnsmasq configuration");
    }
}

app.UseCors("ui");
app.MapControllers();
app.MapOpenApi();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }));

await app.RunAsync();
