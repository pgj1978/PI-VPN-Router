# PiRouter Setup Guide

This guide walks you through setting up PiRouter on a fresh Raspberry Pi 5.

## Prerequisites

- **Raspberry Pi 5** with fresh Raspberry Pi OS (Bullseye or later)
- **Two Ethernet interfaces**:
  - `eth0` (built-in): for LAN (connected devices)
  - `eth1` (USB adapter): for WAN (internet connection)
- **SSH access** to the Pi or physical console access
- **Root/sudo privileges**

## Quick Start

### 1. Clone the Repository

```bash
git clone <repository-url> ~/code/PiRouter
cd ~/code/PiRouter
```

### 2. Run the Setup Script

```bash
sudo bash tools/setup_pi.sh
```

The script will:
- ✓ Check for required software (Docker, dnsmasq, WireGuard)
- ✓ Install missing dependencies
- ✓ Configure network interfaces (eth0 as LAN, eth1 as WAN)
- ✓ Set up DHCP server (dnsmasq)
- ✓ Configure WireGuard directory
- ✓ Enable IP forwarding
- ✓ Set up firewall rules (iptables)
- ✓ Make firewall rules persistent

### 3. Deploy the Application

```bash
cd ~/code/PiRouter
docker-compose up -d
```

### 4. Access the Web Interface

Open your browser to:
- **Web UI**: `http://192.168.100.1:4200`
- **API**: `http://192.168.100.1:51508`

## Setup Script Options

Skip certain setup steps if already configured:

```bash
# Skip network configuration
sudo bash tools/setup_pi.sh --skip-network

# Skip Docker installation
sudo bash tools/setup_pi.sh --skip-docker

# Skip both
sudo bash tools/setup_pi.sh --skip-network --skip-docker
```

## Network Configuration

### Default Network Setup

The script configures:

| Interface | Purpose | IP Address | Configuration |
|-----------|---------|------------|----------------|
| eth0 | LAN (Local Devices) | 192.168.100.1 | Static |
| eth1 | WAN (Internet) | DHCP | Dynamic |

### DHCP Configuration

- **DHCP Server**: dnsmasq (on eth0)
- **IP Range**: 192.168.100.50 - 192.168.100.200
- **Lease Time**: 24 hours
- **DNS Servers**: 1.1.1.1, 8.8.8.8

### Manual Network Changes

If you need different network settings, edit the configuration files:

1. **eth0 (LAN)**: `/etc/network/interfaces.d/eth0`
2. **eth1 (WAN)**: `/etc/network/interfaces.d/eth1`
3. **dnsmasq**: `/etc/dnsmasq.conf`

Then restart networking:
```bash
sudo systemctl restart networking
sudo systemctl restart dnsmasq
```

## WireGuard VPN Setup

### Add VPN Profiles

1. Place WireGuard `.conf` files in `wireguard_configs/` directory
2. Files should follow standard WireGuard format:

```ini
[Interface]
PrivateKey = <your-private-key>
Address = 10.x.x.x/32
DNS = 8.8.8.8

[Peer]
PublicKey = <peer-public-key>
Endpoint = vpn.example.com:51820
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
```

3. Access the web UI to select and connect to profiles

### Manual WireGuard Commands

```bash
# Check WireGuard status
sudo wg show

# View WireGuard interface
ip addr show wg0

# View routing table for VPN tunnel
ip route show table 51820  # table ID may vary
```

## Static IP Assignment

Assign static DHCP leases to devices using the helper script:

```bash
# Assign static IP
sudo tools/agent-scripts/safe-static-lease.sh 00:d8:61:34:29:8a 192.168.100.50

# Remove static lease (leave IP empty)
sudo tools/agent-scripts/safe-static-lease.sh 00:d8:61:34:29:8a ""
```

This will:
1. Update static lease configuration
2. Stop dnsmasq
3. Clear the old lease from dnsmasq database
4. Restart dnsmasq

## Managing Docker Services

```bash
# Start all services
docker-compose up -d

# Stop all services
docker-compose down

# Restart services
docker-compose restart

# View logs
docker-compose logs -f

# View specific service logs
docker-compose logs -f backend-csharp
docker-compose logs -f frontend

# Rebuild containers
docker-compose build --no-cache backend-csharp
docker-compose up -d backend-csharp
```

## Firewall & Routing

### View Current Rules

```bash
# View NAT rules
sudo iptables -t nat -L POSTROUTING -v

# View forward rules
sudo iptables -L FORWARD -v

# View all interfaces and IPs
ip addr show

# View routing table
ip route show

# View policy-based routing tables
ip rule list
ip route show table main
ip route show table 51820  # WireGuard table (may vary)
```

### Firewall Rules

The setup script configures:

1. **NAT (Masquerading)**:
   - `eth1` → `eth0` (LAN traffic)
   - `eth1` → `wg0` (VPN traffic)

2. **Forwarding**:
   - Allow traffic from `eth1` to `eth0` and `wg0`

3. **Persistence**:
   - Rules are saved to `/etc/iptables/rules.v4`
   - Automatic restore on boot via systemd service

### Manual Firewall Configuration

```bash
# View current rules
sudo iptables -t nat -L -v

# Add a rule
sudo iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE

# Save rules
sudo iptables-save > /etc/iptables/rules.v4

# Restore rules
sudo iptables-restore < /etc/iptables/rules.v4
```

## Troubleshooting

### Web Interface Not Accessible

1. Check containers are running:
   ```bash
   docker-compose ps
   ```

2. Check frontend logs:
   ```bash
   docker-compose logs frontend
   ```

3. Verify port 4200 is open:
   ```bash
   sudo netstat -tlnp | grep :4200
   ```

4. Test API directly:
   ```bash
   curl http://localhost:51508/api/vpn/status
   ```

### No Internet on Client Devices

1. Verify IP forwarding is enabled:
   ```bash
   cat /proc/sys/net/ipv4/ip_forward
   ```
   Should be `1`

2. Check eth1 has internet:
   ```bash
   ping -I eth1 8.8.8.8
   ```

3. Verify NAT rules:
   ```bash
   sudo iptables -t nat -L POSTROUTING -v
   ```

4. Check dnsmasq is running:
   ```bash
   sudo systemctl status dnsmasq
   ```

### VPN Not Connecting

1. Check WireGuard config:
   ```bash
   sudo cat /etc/wireguard/wg0.conf
   ```

2. Test manually:
   ```bash
   sudo wg-quick up wg0
   sudo wg show
   sudo wg-quick down wg0
   ```

3. Check backend logs:
   ```bash
   docker-compose logs -f backend-csharp
   ```

### Static IP Assignment Not Working

1. Verify static lease file:
   ```bash
   cat /etc/dnsmasq.d/02-static-leases.conf
   ```

2. Check dnsmasq is running:
   ```bash
   sudo systemctl status dnsmasq
   ```

3. Force client to renew:
   - **Windows**: `ipconfig /release && ipconfig /renew`
   - **Linux**: `sudo dhclient -r eth0 && sudo dhclient eth0`
   - **macOS**: System Preferences → Network → Renew DHCP Lease

## System Directories

Important paths on the Pi:

```
/etc/dnsmasq.conf              - Main DHCP/DNS config
/etc/dnsmasq.d/                - Additional dnsmasq configs
/etc/dnsmasq.d/02-static-leases.conf - Static IP leases
/etc/network/interfaces.d/     - Network interface configs
/etc/wireguard/                - WireGuard configs
/etc/iptables/rules.v4         - Firewall rules (persistent)
/var/lib/misc/dnsmasq.leases   - Active DHCP leases
/etc/systemd/system/           - Systemd service files
~/code/PiRouter/               - Project root
~/code/PiRouter/wireguard_configs/ - VPN profiles
~/code/PiRouter/backend/config/    - Backend configuration
```

## Advanced Configuration

### Change Network Subnet

Edit `/etc/dnsmasq.conf` and change:
```conf
dhcp-range=192.168.100.50,192.168.100.200,24h
dhcp-option=option:router,192.168.100.1
```

Edit `/etc/network/interfaces.d/eth0`:
```
iface eth0 inet static
    address 192.168.X.1
    netmask 255.255.255.0
```

### Enable DNS Logging

Edit `/etc/dnsmasq.conf`:
```conf
log-queries
log-facility=/var/log/dnsmasq.log
```

### Custom DNS Servers

Edit `/etc/dnsmasq.conf`:
```conf
server=8.8.8.8
server=8.8.4.4
```

## Security Recommendations

1. **Change Pi Password**:
   ```bash
   passwd
   ```

2. **Enable SSH Key Authentication**:
   ```bash
   # Copy public key to authorized_keys
   cat ~/.ssh/id_rsa.pub | ssh pgj99@192.168.100.1 "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys"
   ```

3. **Restrict SSH Access**:
   Edit `/etc/ssh/sshd_config` and restrict listening address

4. **Firewall Access Control**:
   Only open necessary ports to trusted networks

5. **HTTPS for Web UI**:
   Consider adding SSL/TLS certificates for production use

## Getting Help

If you encounter issues:

1. Check logs:
   ```bash
   docker-compose logs
   systemctl status dnsmasq
   journalctl -xe
   ```

2. Review network configuration:
   ```bash
   ip addr show
   ip route show
   sudo iptables -t nat -L -v
   ```

3. Test connectivity:
   ```bash
   ping 8.8.8.8
   ping 192.168.100.1  # from a client
   curl http://localhost:51508/api/vpn/status
   ```

4. Check system resources:
   ```bash
   free -h
   df -h
   docker stats
   ```

## Next Steps

1. Deploy the application: `docker-compose up -d`
2. Add WireGuard VPN profiles to `wireguard_configs/`
3. Access web UI at `http://192.168.100.1:4200`
4. Configure devices for VPN routing
5. Test connectivity from client devices
