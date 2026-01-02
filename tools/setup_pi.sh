#!/bin/bash
set -euo pipefail

# PiRouter Setup Script for Fresh Raspberry Pi
# This script checks for and installs all required dependencies and configurations
# Run with: sudo bash setup_pi.sh [--skip-network] [--skip-docker]

SKIP_NETWORK=0
SKIP_DOCKER=0
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT_DIR="$(dirname "${BASH_SOURCE[0]}")"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration defaults
LAN_IP="192.168.100.1"
LAN_NETMASK="255.255.255.0"
LAN_SUBNET="192.168.100.0/24"
DHCP_START="192.168.100.50"
DHCP_END="192.168.100.200"
DNS_SERVERS="1.1.1.1,8.8.8.8"

# Parse arguments
for arg in "$@"; do
  case $arg in
    --skip-network) SKIP_NETWORK=1 ;;
    --skip-docker) SKIP_DOCKER=1 ;;
    *) echo "Unknown option: $arg"; exit 1 ;;
  esac
done

# Helper functions
log_info() {
  echo -e "${BLUE}[INFO]${NC} $1"
}

log_success() {
  echo -e "${GREEN}[✓]${NC} $1"
}

log_error() {
  echo -e "${RED}[ERROR]${NC} $1"
}

log_warning() {
  echo -e "${YELLOW}[WARNING]${NC} $1"
}

check_root() {
  if [ "$(id -u)" != "0" ]; then
    log_error "This script must be run as root. Use: sudo bash $0"
    exit 1
  fi
}

check_pi() {
  if ! grep -q "Raspberry Pi" /proc/device-tree/model 2>/dev/null; then
    log_warning "This does not appear to be a Raspberry Pi. Some features may not work."
  else
    log_success "Detected: $(cat /proc/device-tree/model)"
  fi
}

# Installation checks
check_and_install_docker() {
  if [ $SKIP_DOCKER -eq 1 ]; then
    log_info "Skipping Docker installation (--skip-docker)"
    return
  fi

  log_info "Checking Docker installation..."
  
  if command -v docker &> /dev/null; then
    DOCKER_VERSION=$(docker --version)
    log_success "Docker is installed: $DOCKER_VERSION"
  else
    log_info "Installing Docker..."
    curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
    bash /tmp/get-docker.sh
    log_success "Docker installed"
  fi

  # Check Docker Compose
  if docker compose version &> /dev/null; then
    COMPOSE_VERSION=$(docker compose version)
    log_success "Docker Compose is installed: $COMPOSE_VERSION"
  else
    log_warning "Docker Compose not found, installing..."
    apt-get update
    apt-get install -y docker-compose
    log_success "Docker Compose installed"
  fi
}

check_and_install_dnsmasq() {
  log_info "Checking dnsmasq installation..."

  if command -v dnsmasq &> /dev/null; then
    log_success "dnsmasq is installed"
  else
    log_info "Installing dnsmasq..."
    apt-get update
    apt-get install -y dnsmasq
    log_success "dnsmasq installed"
  fi

  # Check if dnsmasq service exists
  if systemctl list-unit-files | grep -q "dnsmasq.service"; then
    log_success "dnsmasq service is available"
  fi
}

check_and_install_wireguard() {
  log_info "Checking WireGuard installation..."

  if command -v wg &> /dev/null; then
    log_success "WireGuard is installed"
  else
    log_info "Installing WireGuard..."
    apt-get update
    apt-get install -y wireguard wireguard-tools
    log_success "WireGuard installed"
  fi

  # Check for wg-quick
  if command -v wg-quick &> /dev/null; then
    log_success "wg-quick is available"
  fi
}

check_and_install_networking_tools() {
  log_info "Checking network management tools..."

  TOOLS=("iproute2" "iptables" "nftables" "curl" "wget")
  MISSING_TOOLS=()

  for tool in "${TOOLS[@]}"; do
    if ! dpkg -l | grep -q "^ii  $tool"; then
      MISSING_TOOLS+=("$tool")
    else
      log_success "$tool is installed"
    fi
  done

  if [ ${#MISSING_TOOLS[@]} -gt 0 ]; then
    log_info "Installing missing tools: ${MISSING_TOOLS[*]}"
    apt-get update
    apt-get install -y "${MISSING_TOOLS[@]}"
    log_success "Tools installed"
  fi
}

check_ip_forwarding() {
  log_info "Checking IP forwarding..."

  CURRENT_VALUE=$(cat /proc/sys/net/ipv4/ip_forward)
  
  if [ "$CURRENT_VALUE" = "1" ]; then
    log_success "IP forwarding is enabled"
  else
    log_warning "IP forwarding is disabled, enabling..."
    echo 1 > /proc/sys/net/ipv4/ip_forward
    
    # Make it permanent
    if grep -q "net.ipv4.ip_forward" /etc/sysctl.conf; then
      sed -i 's/^#net.ipv4.ip_forward=.*/net.ipv4.ip_forward=1/' /etc/sysctl.conf
      sed -i 's/^net.ipv4.ip_forward=.*/net.ipv4.ip_forward=1/' /etc/sysctl.conf
    else
      echo "net.ipv4.ip_forward=1" >> /etc/sysctl.conf
    fi
    
    log_success "IP forwarding enabled"
  fi
}

check_interfaces() {
  log_info "Checking network interfaces..."

  if ip link show eth0 &> /dev/null; then
    log_success "eth0 found"
  else
    log_error "eth0 not found"
    return 1
  fi

  if ip link show eth1 &> /dev/null; then
    log_success "eth1 found"
  else
    log_warning "eth1 not found (USB Ethernet adapter may not be connected)"
  fi

  return 0
}

setup_network_config() {
  if [ $SKIP_NETWORK -eq 1 ]; then
    log_info "Skipping network configuration (--skip-network)"
    return
  fi

  log_info "Setting up network configuration..."

  # Check if eth0 already has an IP in the expected range
  ETH0_IP=$(ip addr show eth0 2>/dev/null | grep "inet " | awk '{print $2}' | cut -d'/' -f1)
  
  if [ -n "$ETH0_IP" ] && [ "$ETH0_IP" != "127.0.0.1" ]; then
    log_warning "eth0 already has IP: $ETH0_IP"
    read -p "Do you want to keep this configuration? (y/n) " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
      log_info "Keeping existing eth0 configuration"
      return
    fi
  fi

  log_info "Configuring eth0 as LAN interface (static IP)..."
  
  # Create/update network configuration
  cat > /etc/network/interfaces.d/eth0 << EOF
auto eth0
iface eth0 inet static
    address $LAN_IP
    netmask $LAN_NETMASK
    # gateway and dns are handled by dnsmasq
EOF

  log_success "eth0 configuration created"

  log_info "Configuring eth1 as WAN interface..."
  cat > /etc/network/interfaces.d/eth1 << EOF
auto eth1
iface eth1 inet dhcp
EOF

  log_success "eth1 configuration created"

  log_info "Applying network configuration..."
  if command -v systemctl &> /dev/null && systemctl is-active --quiet networking; then
    systemctl restart networking || log_warning "Could not restart networking service"
  fi
  
  # Alternative: use ip command
  ip addr flush eth0 2>/dev/null || true
  ip addr add "$LAN_SUBNET" dev eth0 2>/dev/null || log_warning "Could not set eth0 IP"
  ip link set eth0 up
  
  log_success "Network configuration applied"
}

setup_dnsmasq_config() {
  log_info "Setting up dnsmasq configuration..."

  # Create dnsmasq config directory if it doesn't exist
  mkdir -p /etc/dnsmasq.d

  # Backup original if it exists
  if [ -f /etc/dnsmasq.conf ]; then
    if ! grep -q "pirouter" /etc/dnsmasq.conf; then
      cp /etc/dnsmasq.conf /etc/dnsmasq.conf.backup.$(date +%s)
    fi
  fi

  # Create main dnsmasq config for PiRouter
  cat > /etc/dnsmasq.conf << 'EOF'
# PiRouter dnsmasq configuration
# Generated by setup_pi.sh

# Interface configuration
interface=eth0
bind-interfaces
listen-address=192.168.100.1

# DHCP configuration
dhcp-range=192.168.100.50,192.168.100.200,24h
dhcp-option=option:router,192.168.100.1
dhcp-option=option:dns-server,1.1.1.1,8.8.8.8

# DNS configuration
no-resolv
server=1.1.1.1
server=8.8.8.8

# Cache settings
cache-size=1000
neg-ttl=600

# Log queries (optional, disable for production)
# log-queries

# Static leases
conf-dir=/etc/dnsmasq.d,*.conf
EOF

  log_success "dnsmasq configuration created"

  # Create static leases directory
  mkdir -p /etc/dnsmasq.d
  touch /etc/dnsmasq.d/02-static-leases.conf
  chmod 644 /etc/dnsmasq.d/02-static-leases.conf

  # Start/restart dnsmasq
  if systemctl is-enabled dnsmasq &> /dev/null; then
    log_info "Restarting dnsmasq service..."
    systemctl restart dnsmasq
    log_success "dnsmasq restarted"
  else
    log_warning "dnsmasq service not enabled, enabling..."
    systemctl enable dnsmasq
    systemctl start dnsmasq
    log_success "dnsmasq enabled and started"
  fi
}

setup_wireguard_config() {
  log_info "Setting up WireGuard directory..."

  mkdir -p /etc/wireguard
  chmod 700 /etc/wireguard

  # Create empty wg0.conf if it doesn't exist
  if [ ! -f /etc/wireguard/wg0.conf ]; then
    cat > /etc/wireguard/wg0.conf << 'EOF'
# WireGuard configuration for PiRouter
# Add your VPN configuration here
# This file will be managed by PiRouter backend

[Interface]
PrivateKey = PLACEHOLDER_KEY
Address = 10.0.0.2/32
# DNS = 8.8.8.8

# [Peer]
# PublicKey = PLACEHOLDER_KEY
# Endpoint = vpn.example.com:51820
# AllowedIPs = 0.0.0.0/0
# PersistentKeepalive = 25
EOF
    chmod 600 /etc/wireguard/wg0.conf
    log_success "WireGuard config created (placeholder)"
  else
    log_success "WireGuard config exists"
  fi
}

setup_project_directories() {
  log_info "Setting up project directories..."

  # Create necessary directories if they don't exist
  mkdir -p "$REPO_ROOT/backend/config/vpn_profiles"
  mkdir -p "$REPO_ROOT/wireguard_configs"

  # Ensure proper permissions
  if [ -d "$REPO_ROOT/wireguard_configs" ]; then
    chmod 700 "$REPO_ROOT/wireguard_configs"
  fi

  log_success "Project directories ready"
}

setup_firewall_rules() {
  log_info "Setting up firewall rules (iptables)..."

  # Enable NAT for eth1 -> eth0/wg0
  iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE || log_warning "Could not set eth0 NAT rule"
  iptables -t nat -A POSTROUTING -o wg0 -j MASQUERADE || log_warning "Could not set wg0 NAT rule (VPN may not be active)"

  # Accept forwarded packets
  iptables -A FORWARD -i eth1 -o eth0 -j ACCEPT || log_warning "Could not set forwarding rule"
  iptables -A FORWARD -i eth1 -o wg0 -j ACCEPT || log_warning "Could not set VPN forwarding rule"

  # Make iptables rules persistent
  if command -v iptables-save &> /dev/null; then
    mkdir -p /etc/iptables
    iptables-save > /etc/iptables/rules.v4 || log_warning "Could not save iptables rules"
    
    # Create systemd service to restore rules on boot
    if [ ! -f /etc/systemd/system/restore-iptables.service ]; then
      cat > /etc/systemd/system/restore-iptables.service << 'EOF'
[Unit]
Description=Restore iptables rules
Before=docker.service
After=network.target

[Service]
Type=oneshot
ExecStart=/sbin/iptables-restore < /etc/iptables/rules.v4
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF
      systemctl daemon-reload
      systemctl enable restore-iptables.service
      log_success "Created iptables persistence service"
    fi
  fi

  log_success "Firewall rules configured"
}

setup_helper_scripts() {
  log_info "Setting up helper scripts..."

  # Make scripts executable
  chmod +x "$SCRIPT_DIR/safe-static-lease.sh"
  log_success "Helper scripts ready"
}

print_summary() {
  echo
  echo -e "${BLUE}════════════════════════════════════════${NC}"
  echo -e "${GREEN}PiRouter Setup Complete!${NC}"
  echo -e "${BLUE}════════════════════════════════════════${NC}"
  echo
  echo "Next steps:"
  echo
  echo "1. Deploy the application:"
  echo "   cd $REPO_ROOT"
  echo "   docker-compose up -d"
  echo
  echo "2. Add WireGuard VPN profiles:"
  echo "   Copy .conf files to $REPO_ROOT/wireguard_configs/"
  echo
  echo "3. Access the web interface:"
  echo "   http://$LAN_IP:4200"
  echo
  echo "4. Access the API:"
  echo "   http://$LAN_IP:51508"
  echo
  echo "Configuration locations:"
  echo "  - dnsmasq: /etc/dnsmasq.conf"
  echo "  - Network: /etc/network/interfaces.d/"
  echo "  - WireGuard: /etc/wireguard/"
  echo "  - Static leases: /etc/dnsmasq.d/02-static-leases.conf"
  echo
  echo "Helper script for static IPs:"
  echo "  sudo $SCRIPT_DIR/safe-static-lease.sh <MAC> <IP>"
  echo
}

main() {
  echo -e "${BLUE}"
  echo "╔══════════════════════════════════════════╗"
  echo "║  PiRouter Setup Script for Raspberry Pi  ║"
  echo "╚══════════════════════════════════════════╝"
  echo -e "${NC}"
  echo

  check_root
  check_pi

  echo
  log_info "Starting system checks..."
  echo

  check_interfaces || log_warning "Interface check failed, continuing..."
  check_and_install_docker
  check_and_install_dnsmasq
  check_and_install_wireguard
  check_and_install_networking_tools
  check_ip_forwarding

  echo
  log_info "Configuring system..."
  echo

  setup_network_config
  setup_dnsmasq_config
  setup_wireguard_config
  setup_project_directories
  setup_firewall_rules
  setup_helper_scripts

  echo
  print_summary
}

# Run main function
main "$@"
