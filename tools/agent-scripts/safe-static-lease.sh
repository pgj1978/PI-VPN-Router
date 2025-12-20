#!/bin/bash
set -euo pipefail

# Safe static lease updater for PiRouter
# Usage: sudo ./safe-static-lease.sh <MAC> <IP>
# To remove static lease: sudo ./safe-static-lease.sh <MAC> ""

MAC="$1"
IP="${2-}"

STATIC_FILE="/etc/dnsmasq.d/02-static-leases.conf"
LEASE_FILE="/var/lib/misc/dnsmasq.leases"
DNSMASQ_DB_DIR="/var/lib/dnsmasq"
TMP="/tmp/02-static-leases.conf.$$"

if [ -z "$MAC" ]; then
  echo "Usage: $0 <MAC> <IP>"
  exit 2
fi

# Normalize MAC to lowercase
MAC_LOWER=$(echo "$MAC" | tr '[:upper:]' '[:lower:]')

# Ensure static file exists
mkdir -p "$(dirname "$STATIC_FILE")"
if [ ! -f "$STATIC_FILE" ]; then
  touch "$STATIC_FILE"
  chmod 644 "$STATIC_FILE"
fi

# Update static leases file: remove previous entry for MAC, then optionally add new
awk -v mac="$MAC_LOWER" 'BEGIN{IGNORECASE=1} !(/^dhcp-host=/) {print; next} {line=tolower($0); sub(/^dhcp-host=/, "", line); split(line, parts, ","); if (parts[1] != mac) print $0}' "$STATIC_FILE" > "$TMP"

if [ -n "$IP" ]; then
  # Validate IP with simple regex
  if ! echo "$IP" | grep -E "^[0-9]+(\.[0-9]+){3}$" >/dev/null; then
    echo "Invalid IP address: $IP"
    rm -f "$TMP"
    exit 3
  fi
  echo "dhcp-host=${MAC_LOWER},${IP}" >> "$TMP"
fi

mv "$TMP" "$STATIC_FILE"
chmod 644 "$STATIC_FILE"

# Stop dnsmasq
if command -v systemctl >/dev/null 2>&1; then
  systemctl stop dnsmasq || true
elif command -v service >/dev/null 2>&1; then
  service dnsmasq stop || true
elif [ -x "/etc/init.d/dnsmasq" ]; then
  /etc/init.d/dnsmasq stop || true
fi
sleep 1

# Remove existing lease for this MAC from lease file
if [ -f "$LEASE_FILE" ]; then
  # keep lines where second field != MAC (case-insensitive)
  awk -v mac="$MAC_LOWER" 'BEGIN{IGNORECASE=1} {if ($2 != mac) print $0}' "$LEASE_FILE" > "$LEASE_FILE.tmp.$$" && mv "$LEASE_FILE.tmp.$$" "$LEASE_FILE"
fi

# Clear dnsmasq db/cache files if present
if [ -d "$DNSMASQ_DB_DIR" ]; then
  rm -f "$DNSMASQ_DB_DIR"/* || true
fi

# Start dnsmasq
if command -v systemctl >/dev/null 2>&1; then
  systemctl start dnsmasq || true
elif command -v service >/dev/null 2>&1; then
  service dnsmasq start || true
elif [ -x "/etc/init.d/dnsmasq" ]; then
  /etc/init.d/dnsmasq start || true
fi

sleep 1

# Try to trigger a reload if possible
if command -v pkill >/dev/null 2>&1; then
  pkill -USR1 dnsmasq || true
fi

echo "Static lease updated for $MAC_LOWER -> ${IP:-<removed>}"
exit 0
