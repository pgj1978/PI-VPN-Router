#!/usr/bin/env bash
#
# PiRouter host preparation.
#
# Everything a container genuinely cannot do for itself, and nothing else. Safe to re-run:
# every step checks the current state first and only acts when something needs changing.
#
#   sudo ./install.sh              prepare the host
#   sudo ./install.sh --check      report what would change, touch nothing
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="$SCRIPT_DIR/.env"
BACKUP_DIR="/var/backups/pirouter"
CHECK_ONLY=0

for arg in "$@"; do
  case "$arg" in
    --check) CHECK_ONLY=1 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

RED=$'\033[0;31m'; GREEN=$'\033[0;32m'; YELLOW=$'\033[1;33m'; BLUE=$'\033[0;34m'; NC=$'\033[0m'
info()  { printf '%s[..]%s %s\n' "$BLUE" "$NC" "$1"; }
ok()    { printf '%s[ok]%s %s\n' "$GREEN" "$NC" "$1"; }
warn()  { printf '%s[!!]%s %s\n' "$YELLOW" "$NC" "$1"; }
fail()  { printf '%s[XX]%s %s\n' "$RED" "$NC" "$1" >&2; }
die()   { fail "$1"; exit 1; }

would() {
  if [ "$CHECK_ONLY" -eq 1 ]; then
    warn "would: $1"
    return 1
  fi
  return 0
}

# ---------------------------------------------------------------- preflight

[ "$(id -u)" -eq 0 ] || die "Run this with sudo."

[ -f "$ENV_FILE" ] || die "No .env found. Copy .env.example to .env and edit it first."

# shellcheck disable=SC1090
set -a; source "$ENV_FILE"; set +a

: "${LAN_INTERFACE:?LAN_INTERFACE must be set in .env}"
: "${WAN_INTERFACE:?WAN_INTERFACE must be set in .env}"
: "${LAN_ADDRESS:?LAN_ADDRESS must be set in .env}"

LAN_IP_ONLY="${LAN_ADDRESS%%/*}"
LAN_PREFIX="${LAN_ADDRESS##*/}"

echo
echo "PiRouter host setup"
echo "  LAN : $LAN_INTERFACE  $LAN_ADDRESS"
echo "  WAN : $WAN_INTERFACE"
[ "$CHECK_ONLY" -eq 1 ] && warn "check mode - nothing will be changed"
echo

# ---------------------------------------------------------------- interfaces

info "Checking interfaces"
ip link show "$WAN_INTERFACE" >/dev/null 2>&1 \
  || die "WAN interface '$WAN_INTERFACE' does not exist. Fix WAN_INTERFACE in .env."
ok "$WAN_INTERFACE exists"

ip link show "$LAN_INTERFACE" >/dev/null 2>&1 \
  || die "LAN interface '$LAN_INTERFACE' does not exist. If it is a USB adapter, plug it in."
ok "$LAN_INTERFACE exists"

# ---------------------------------------------------------------- kernel

info "Checking WireGuard kernel support"
if lsmod 2>/dev/null | grep -q '^wireguard' || modinfo wireguard >/dev/null 2>&1; then
  ok "WireGuard module available"
else
  if would "install wireguard kernel module"; then
    apt-get update -qq && apt-get install -y wireguard wireguard-tools
    modprobe wireguard || die "Could not load the wireguard module."
    ok "WireGuard installed"
  fi
fi

info "Checking IP forwarding"
if [ "$(cat /proc/sys/net/ipv4/ip_forward)" = "1" ]; then
  ok "IP forwarding already enabled"
else
  if would "enable net.ipv4.ip_forward"; then
    sysctl -w net.ipv4.ip_forward=1 >/dev/null
    ok "IP forwarding enabled for this boot"
  fi
fi

# Persist it regardless, so it survives a reboot.
SYSCTL_FILE=/etc/sysctl.d/99-pirouter.conf
if [ ! -f "$SYSCTL_FILE" ] || ! grep -q '^net.ipv4.ip_forward=1' "$SYSCTL_FILE"; then
  if would "write $SYSCTL_FILE"; then
    printf '# Managed by PiRouter\nnet.ipv4.ip_forward=1\n' > "$SYSCTL_FILE"
    ok "IP forwarding persisted in $SYSCTL_FILE"
  fi
else
  ok "IP forwarding already persisted"
fi

# ---------------------------------------------------------------- LAN address

info "Checking the LAN address"
CURRENT_LAN="$(ip -4 -o addr show dev "$LAN_INTERFACE" | awk '{print $4}' | head -1 || true)"

if [ "$CURRENT_LAN" = "$LAN_ADDRESS" ]; then
  ok "$LAN_INTERFACE already has $LAN_ADDRESS"
else
  if would "set $LAN_INTERFACE to $LAN_ADDRESS (currently ${CURRENT_LAN:-none})"; then
    ip addr flush dev "$LAN_INTERFACE"
    ip addr add "$LAN_ADDRESS" dev "$LAN_INTERFACE"
    ip link set "$LAN_INTERFACE" up
    ok "$LAN_INTERFACE set to $LAN_ADDRESS"
  fi
fi

# Persist via systemd-networkd, which is what Debian 12 on the Pi uses.
NETWORK_FILE="/etc/systemd/network/10-${LAN_INTERFACE}.network"
DESIRED_NETWORK="[Match]
Name=${LAN_INTERFACE}

[Network]
Address=${LAN_ADDRESS}
ConfigureWithoutCarrier=yes
"
if [ -f "$NETWORK_FILE" ] && [ "$(cat "$NETWORK_FILE")" = "$DESIRED_NETWORK" ]; then
  ok "LAN address already persisted"
else
  if would "write $NETWORK_FILE"; then
    printf '%s' "$DESIRED_NETWORK" > "$NETWORK_FILE"
    systemctl enable systemd-networkd >/dev/null 2>&1 || true
    ok "LAN address persisted in $NETWORK_FILE"
  fi
fi

# ---------------------------------------------------------------- legacy firewall state

# This is the important one. The previous setup left rules in /etc/iptables/rules.v4 that
# netfilter-persistent restored at every boot, while the application separately added and
# removed rules at runtime. Two owners, no reconciliation. The live result was a MASQUERADE
# rule for 192.168.10.0/24 on a 192.168.20.0/24 LAN (so it matched nothing) and a FORWARD
# chain holding four identical copies of several rules.
#
# PiRouter now owns its own chains and rebuilds them wholesale, so these leftovers must go
# or they will keep fighting it.
info "Checking for stale persisted firewall rules"

STALE=0
if [ -f /etc/iptables/rules.v4 ]; then
  if grep -qE 'MASQUERADE|FORWARD' /etc/iptables/rules.v4 2>/dev/null; then
    STALE=1
  fi
fi

if [ "$STALE" -eq 1 ]; then
  warn "Found persisted rules in /etc/iptables/rules.v4"
  if would "back up and clear the persisted rules"; then
    mkdir -p "$BACKUP_DIR"
    STAMP="$(date +%Y%m%d-%H%M%S)"
    cp /etc/iptables/rules.v4 "$BACKUP_DIR/rules.v4.$STAMP"
    iptables-save > "$BACKUP_DIR/live-rules.v4.$STAMP"
    ok "Backed up to $BACKUP_DIR/rules.v4.$STAMP"

    # Keep the file, but empty of policy, so netfilter-persistent has nothing to restore.
    cat > /etc/iptables/rules.v4 <<'EOF'
# Emptied by PiRouter install.sh.
# PiRouter manages its own chains (PIROUTER_*) and rebuilds them on every change.
# Persisting rules here caused two components to fight over the same chains.
# Previous contents are in /var/backups/pirouter/.
*filter
:INPUT ACCEPT [0:0]
:FORWARD DROP [0:0]
:OUTPUT ACCEPT [0:0]
COMMIT
EOF
    ok "Cleared persisted rules (backup kept)"

    # Remove the stale runtime rules too, so a reboot is not needed to be rid of them.
    # Only rules that reference the VPN interface or masquerade a subnet we do not use.
    while read -r rule; do
      [ -z "$rule" ] && continue
      # shellcheck disable=SC2086
      iptables -t nat ${rule/-A/-D} 2>/dev/null || true
    done < <(iptables -t nat -S POSTROUTING | grep -E '^-A.*MASQUERADE' | grep -v "$LAN_IP_ONLY" | grep -v docker | grep -v br- || true)
    ok "Removed stale runtime NAT rules"

    # The persisted file is only half the problem: the same duplicated rules are also live
    # in the running FORWARD chain right now. On this router that chain had grown to 23
    # rules, including four identical copies of "-i eth0 -o wg0 -j ACCEPT".
    #
    # Everything in FORWARD is removed except the jumps Docker owns and the jump into our
    # own chain. FORWARD's policy is left alone, and INPUT is never touched at all.
    REMOVED=0
    while read -r rule; do
      [ -z "$rule" ] && continue
      case "$rule" in
        *DOCKER-USER*|*DOCKER-FORWARD*|*DOCKER*|*PIROUTER_*) continue ;;
      esac
      # shellcheck disable=SC2086
      if iptables ${rule/-A/-D} 2>/dev/null; then
        REMOVED=$((REMOVED + 1))
      fi
    done < <(iptables -S FORWARD | grep '^-A' || true)

    if [ "$REMOVED" -gt 0 ]; then
      ok "Removed $REMOVED stale runtime FORWARD rule(s)"
    else
      ok "No stale runtime FORWARD rules"
    fi
  fi
else
  ok "No stale persisted rules"
fi

# Even when rules.v4 was already clean, the live chain can still hold leftovers from a
# previous run of the old code, so check it independently.
if [ "$CHECK_ONLY" -eq 0 ]; then
  LEFTOVER=$(iptables -S FORWARD | grep '^-A' | grep -vE 'DOCKER|PIROUTER_' | wc -l)
  if [ "$LEFTOVER" -gt 0 ]; then
    warn "$LEFTOVER unmanaged rule(s) remain in FORWARD"
    while read -r rule; do
      [ -z "$rule" ] && continue
      case "$rule" in *DOCKER*|*PIROUTER_*) continue ;; esac
      # shellcheck disable=SC2086
      iptables ${rule/-A/-D} 2>/dev/null || true
    done < <(iptables -S FORWARD | grep '^-A' || true)
    ok "FORWARD chain cleaned"
  fi
fi

# ---------------------------------------------------------------- host dnsmasq

# dnsmasq now runs as a container. A copy still running on the host would hold port 53 and
# 67 and the containerised one would never start.
info "Checking for a host dnsmasq"
if systemctl is-active --quiet dnsmasq 2>/dev/null; then
  warn "dnsmasq is running on the host and will conflict with the container"
  if would "stop and disable the host dnsmasq"; then
    systemctl stop dnsmasq
    systemctl disable dnsmasq >/dev/null 2>&1 || true
    ok "Host dnsmasq stopped and disabled"
  fi
else
  ok "No host dnsmasq running"
fi

# ---------------------------------------------------------------- docker

info "Checking Docker"
command -v docker >/dev/null 2>&1 || die "Docker is not installed. See https://get.docker.com"
docker compose version >/dev/null 2>&1 || die "The docker compose plugin is not available."
ok "Docker and compose present"

# ---------------------------------------------------------------- directories

info "Preparing directories"
for dir in "$SCRIPT_DIR/config" "$SCRIPT_DIR/vpn_profiles" "$SCRIPT_DIR/dnsmasq.d" "$SCRIPT_DIR/dnsmasq.d/static-leases.d"; do
  if [ -d "$dir" ]; then
    ok "$(basename "$dir") exists"
  elif would "create $dir"; then
    mkdir -p "$dir"
    ok "Created $dir"
  fi
done

mkdir -p /var/lib/misc /etc/wireguard 2>/dev/null || true
chmod 700 /etc/wireguard 2>/dev/null || true

# ---------------------------------------------------------------- done

echo
if [ "$CHECK_ONLY" -eq 1 ]; then
  warn "Check complete - nothing was changed."
else
  ok "Host is ready."
  echo
  echo "  Next:  cd $SCRIPT_DIR && docker compose up -d --build"
  echo "  Then:  http://${LAN_IP_ONLY}:${UI_PORT:-4200}"
fi
echo
