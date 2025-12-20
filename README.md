# PiRouter - VPN Router Management System

Transform your Raspberry Pi 5 into a powerful VPN router with web-based management.

## ✨ Latest: C# ASP.NET Core Backend

The PiRouter backend is now available in **C#** using ASP.NET Core 8! Both Python and C# backends can run simultaneously.

**→ [Read the C# Backend Guide](docs/START_HERE_CSHARP.md)**

## Features

- **Multiple VPN Servers**: Switch between different WireGuard VPN configurations
- **Per-Device Control**: Choose which devices use the VPN and which bypass it
- **Web Interface**: Modern Angular-based UI for easy management
- **REST API**: C# ASP.NET Core OR Python FastAPI backend for programmatic control
- **Docker Deployment**: Containerized for easy deployment and updates
- **Dual Ethernet**: eth0 for LAN, eth1 (USB) for WAN

## Quick Start

### Deploy C# Backend (Recommended)

```powershell
# From Windows
cd D:\PiRouter
.\deploy-csharp.ps1 -TargetHost "pgj99@192.168.10.1"
```

### Or Deploy with Docker Compose (Both Backends)

```bash
# On Pi
cd ~/code/PiRouter
docker-compose up -d
```

### Access the UI
- Open browser to `http://192.168.10.1`
- C# Backend: `http://192.168.10.1:51508`
- Python Backend: `http://192.168.10.1:51507` (if running)

## Documentation

All documentation has been organized in the `docs/` folder:

- **[docs/START_HERE_CSHARP.md](docs/START_HERE_CSHARP.md)** - Start here! Quick overview
- **[docs/CSHARP_QUICKSTART.md](docs/CSHARP_QUICKSTART.md)** - 5-minute deployment guide
- **[docs/CSHARP_MIGRATION.md](docs/CSHARP_MIGRATION.md)** - Complete migration strategy
- **[docs/CSHARP_FILE_INDEX.md](docs/CSHARP_FILE_INDEX.md)** - Code organization reference
- **[docs/CSHARP_DEPLOYMENT_CHECKLIST.md](docs/CSHARP_DEPLOYMENT_CHECKLIST.md)** - Step-by-step deployment
- **[docs/CSHARP_TEST_RESULTS.md](docs/CSHARP_TEST_RESULTS.md)** - Test verification results
- **[PiRouterBackend/README.md](PiRouterBackend/README.md)** - C# backend architecture

## Architecture

### Network Setup
```
Internet → [Router 192.168.10.1] → eth1 (USB - WAN)
                                      ↓
                            [Raspberry Pi 5]
                            VPN Router Logic
                                      ↓
                           eth0 (LAN - 192.168.100.1)
                                      ↓
                            Connected Devices
```

### Backend Options

#### C# Backend (ASP.NET Core 8) - RECOMMENDED
- Port: 51508
- Location: `./PiRouterBackend/`
- Features:
  - Strong type safety
  - Better performance
  - Smaller container (~150MB)
  - Can run alongside Python

#### Python Backend (FastAPI) - Legacy
- Port: 51507
- Location: `./backend/`
- Features:
  - Dynamic typing
  - Rapid development
  - Can coexist with C# backend

#### Frontend (Angular)
- Port: 80
- Location: `./frontend/`
- Features:
  - VPN server selection
  - Device management
  - Real-time status updates

#### System Components
- **WireGuard**: VPN client
- **dnsmasq**: DHCP server for LAN
- **iptables**: NAT and routing rules
- **Docker**: Container runtime

## API Endpoints (14 Total)

### VPN Management (`/api/vpn`)
- `GET /configs` - List configurations
- `GET /status` - Current VPN status
- `POST /connect/{name}` - Connect to VPN
- `POST /disconnect` - Disconnect VPN
- `POST /kill-switch?enabled=true` - Toggle kill switch
- `POST /profile` - Add new profile
- `DELETE /profile/{name}` - Delete profile

### Device Management (`/api/devices`)
- `GET /` - List connected devices
- `POST /{mac}/bypass?bypass=true` - Set device bypass

### Domain Management (`/api/domains`)
- `GET /bypass` - List domain bypasses
- `POST /bypass?domain=xxx` - Add domain bypass
- `DELETE /bypass/{domain}` - Remove domain bypass

### System (`/api/system`)
- `GET /info` - Network and routing information

## File Structure

```
PiRouter/
├── docs/                         # Documentation (8 guides)
├── PiRouterBackend/              # C# ASP.NET Core 8 backend
│   ├── Models/
│   ├── Services/
│   ├── Controllers/
│   ├── Dockerfile
│   └── README.md
├── backend/                      # Python FastAPI backend (legacy)
├── frontend/                     # Angular web application
├── wireguard_configs/            # VPN configuration files
├── deploy-csharp.ps1            # C# deployment (Windows)
├── deploy-csharp.sh             # C# deployment (Linux)
├── docker-compose.yml           # Orchestration (both backends)
└── README.md
```

## Configuration

### Network Settings
Edit `setup_pi_router.sh` to change:
- LAN subnet (default: 192.168.100.0/24)
- DHCP range (default: .50-.200)
- DNS servers (default: 1.1.1.1, 8.8.8.8)

### WireGuard Configs
Place `.conf` files in `wireguard_configs/` directory.
Each config should contain:
```ini
[Interface]
PrivateKey = your_private_key
Address = 10.x.x.x/32
DNS = 1.1.1.1

[Peer]
PublicKey = server_public_key
Endpoint = server.example.com:51820
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
```

## Management

### Start/Stop Services
```bash
docker-compose up -d      # Start
docker-compose down       # Stop
docker-compose restart    # Restart
docker-compose logs -f    # View logs
```

### Check VPN Status
```bash
sudo wg show              # WireGuard status
ip route show             # Routing table
sudo iptables -t nat -L   # NAT rules
```

### View Connected Devices
```bash
cat /var/lib/misc/dnsmasq.leases
```

## Deployment

### Deploy C# Backend
```powershell
# From Windows
.\deploy-csharp.ps1 -TargetHost "pgj99@192.168.10.1"
```

### Deploy Both Backends (Docker Compose)
```bash
# On Pi
cd ~/code/PiRouter
docker-compose up -d
```

## Troubleshooting

### VPN Not Connecting
1. Check WireGuard config: `sudo cat /etc/wireguard/wg0.conf`
2. Test manually: `sudo wg-quick up wg0`
3. Check logs: `docker-compose logs backend-csharp`

### No Internet on Clients
1. Verify IP forwarding: `cat /proc/sys/net/ipv4/ip_forward` (should be 1)
2. Check NAT rules: `sudo iptables -t nat -L POSTROUTING -v`
3. Verify eth1 has internet: `ping -I eth1 8.8.8.8`

### Web Interface Not Loading
1. Check frontend container: `docker-compose ps`
2. View nginx logs: `docker-compose logs frontend`
3. Verify port 80 is accessible: `netstat -tlnp | grep :80`

### API Not Responding
1. Check backend logs: `docker-compose logs backend-csharp`
2. Test API: `curl http://localhost:51508/api/vpn/status`
3. Verify permissions for network commands

## Development

### C# Backend Development
```bash
cd PiRouterBackend
dotnet run
# Runs on http://localhost:5000
```

### Python Backend Development
```bash
cd backend
python3 -m venv venv
source venv/bin/activate
pip install -r requirements.txt
uvicorn main:app --reload --host 0.0.0.0
```

### Frontend Development
```bash
cd frontend
npm install
ng serve --host 0.0.0.0
```

## Security Considerations

1. **Change Default Password**: Update SSH password after setup
2. **Firewall**: Consider restricting SSH access
3. **HTTPS**: For production, add SSL/TLS
4. **API Auth**: Currently no authentication - use on trusted networks only
5. **WireGuard Keys**: Keep private keys secure (permissions 600)

## Support

For detailed deployment instructions, see the documentation in the `docs/` folder.

For issues:
- Check logs: `docker-compose logs`
- Verify network: `ip addr show`
- Test VPN: `sudo wg show`
- Review iptables: `sudo iptables -L -v`
