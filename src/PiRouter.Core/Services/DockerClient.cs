using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PiRouter.Core.Services;

public interface IDockerClient
{
    Task<bool> RestartContainerAsync(string name, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Minimal Docker Engine API client over the unix socket.
///
/// This replaces the previous approach of shelling out to nsenter and trying
/// /etc/init.d, then `service`, then `systemctl` in turn and hoping one of them worked.
/// One well-defined interface instead of three fallbacks whose failure modes were invisible.
/// </summary>
public sealed class DockerClient(ILogger<DockerClient> logger) : IDockerClient, IDisposable
{
    private const string SocketPath = "/var/run/docker.sock";

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
    })
    {
        BaseAddress = new Uri("http://localhost"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(SocketPath)) return false;
        try
        {
            using var response = await _http.GetAsync("/_ping", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> RestartContainerAsync(string name, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsync($"/containers/{Uri.EscapeDataString(name)}/restart", null, ct);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Restarted container {Container}", name);
                return true;
            }

            logger.LogWarning("Docker refused to restart {Container}: {Status}", name, response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TaskCanceledException)
        {
            logger.LogWarning("Could not reach the Docker socket to restart {Container}: {Error}", name, ex.Message);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
