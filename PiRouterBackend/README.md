# PiRouter C# Backend

A complete rewrite of the Python FastAPI backend in C# using ASP.NET Core 8, maintaining 100% API compatibility with the original Python implementation.

## Overview

- **Framework**: ASP.NET Core 8 (LTS)
- **Language**: C# 12
- **Port**: 51508 (separate from Python backend on 51507)
- **Runtime**: Alpine Linux on ARM64 (Raspberry Pi 5)
- **Deployment**: Docker container

## Architecture

### Project Structure

```
PiRouterBackend/
├── Models/              # Data models (Device, WireGuardConfig, etc.)
├── Services/            # Business logic (VpnManager, DeviceManager, etc.)
├── Controllers/         # REST API endpoints
├── Dockerfile           # Container build
├── Program.cs           # Configuration and startup
└── PiRouterBackend.csproj
```

### Service Layer

- **IVpnManager**: VPN connection and profile management
- **IDeviceManager**: Device tracking and bypass configuration
- **IDomainManager**: Domain-level split tunneling
- **ISystemManager**: System information
- **IProcessRunner**: Cross-platform process execution with sudo support
- **IConfigManager**: Configuration persistence

## API Endpoints

All endpoints maintain the same URLs as the Python backend for drop-in replacement.

### VPN Management (`/api/vpn`)

- `GET /api/vpn/configs` - List VPN configurations
- `GET /api/vpn/profiles` - List VPN profiles (alternative)
- `GET /api/vpn/status` - Get current VPN status
- `POST /api/vpn/connect/{profileName}` - Connect to VPN
- `POST /api/vpn/disconnect` - Disconnect from VPN
- `POST /api/vpn/kill-switch?enabled=true` - Toggle kill switch
- `POST /api/vpn/profile?name=xxx&configContent=...` - Add profile
- `DELETE /api/vpn/profile/{name}` - Delete profile

### Device Management (`/api/devices`)

- `GET /api/devices` - List connected devices
- `POST /api/devices/{mac}/bypass?bypass=true` - Set device bypass
- `GET /api/devices/{mac}/bypass?bypass=true` - Set device bypass (GET variant)

### Domain Management (`/api/domains`)

- `GET /api/domains/bypass` - List domain bypasses
- `POST /api/domains/bypass?domain=xxx` - Add domain bypass
- `DELETE /api/domains/bypass/{domain}` - Remove domain bypass

### System (`/api/system`)

- `GET /api/system/info` - Get network and routing information

## Development

### Prerequisites

- .NET 8 SDK
- Visual Studio Code with C# extension (optional)

### Build Locally

```bash
cd PiRouterBackend
dotnet build
```

### Run Locally

```bash
dotnet run
```

The API will be available at `http://localhost:51508`

### Testing

```bash
# List VPN profiles
curl http://localhost:51508/api/vpn/profiles

# Get VPN status
curl http://localhost:51508/api/vpn/status

# List devices
curl http://localhost:51508/api/devices
```

## Docker Deployment

### Build for Raspberry Pi 5

```bash
docker build -t pirouter-backend-cs:latest .
```

### Run Container

```bash
docker run -d \
  --name pirouter-backend-cs \
  --network host \
  --privileged \
  -v /etc/wireguard:/etc/wireguard \
  -v /var/lib/misc:/var/lib/misc \
  -v /home/pgj99/code/PiRouter/backend/config:/home/pgj99/code/PiRouter/backend/config \
  pirouter-backend-cs:latest
```

## Configuration

### Environment Variables

- `ASPNETCORE_ENVIRONMENT`: Set to `Production` for deployment
- `ASPNETCORE_URLS`: Defaults to `http://+:51508`
- `ServerPort`: Override port (default: 51508)

### Configuration Files

- `/home/pgj99/code/PiRouter/backend/config/router_config.json` - Main configuration
- `/home/pgj99/code/PiRouter/backend/config/vpn_profiles/` - VPN profile storage
- `/etc/wireguard/wg0.conf` - Active WireGuard configuration

## Key Differences from Python Implementation

1. **Strong Typing**: Full type safety with nullable reference types enabled
2. **Dependency Injection**: Built-in DI container vs manual imports
3. **Async/Await**: Native support throughout, no event loop
4. **Process Management**: `System.Diagnostics.Process` for cross-platform compatibility
5. **Configuration**: JSON serialization with `System.Text.Json`

## Switching Between Python and C# Backends

During development, both backends can run simultaneously:

- Python backend: `http://pi:51507`
- C# backend: `http://pi:51508`

To switch the frontend to the C# backend, update the API base URL in the Angular frontend configuration.

## Error Handling

All endpoints return consistent error responses:

```json
{
  "success": false,
  "error": "Descriptive error message"
}
```

Successful operations return appropriate response objects matching the Python backend exactly.

## Logging

Logs are output to the console and can be captured from Docker:

```bash
docker logs pirouter-backend-cs
```

Configure logging level via `appsettings.json`.

## Future Enhancements

- [ ] Implement device routing rules (iptables integration)
- [ ] Add HTTPS support with certificate management
- [ ] Implement rate limiting
- [ ] Add request/response logging middleware
- [ ] Unit tests for service layer
- [ ] Integration tests with Docker containers

## Troubleshooting

### Connection Refused

Verify the container is running and listening:

```bash
docker ps | grep pirouter-backend-cs
curl http://localhost:51508/
```

### Permission Denied on WireGuard Commands

Ensure the container runs with `--privileged` flag and has access to `/etc/wireguard`.

### Configuration Not Persisting

Check that the config directory is properly mounted and writable:

```bash
docker exec pirouter-backend-cs ls -la /home/pgj99/code/PiRouter/backend/config/
```

## License

Same as main PiRouter project.
