# tools/agent-scripts

Helper scripts intended for automation, bots, or maintainers. These are not part of the production runtime; they are utilities to make administration, debugging, and automated testing easier.

## safe-static-lease.sh

- Purpose: Safely update dnsmasq static DHCP leases on the Pi host and ensure dnsmasq picks up the change immediately.
- Location: edits `/etc/dnsmasq.d/02-static-leases.conf` and operates on `/var/lib/misc/dnsmasq.leases`.
- Usage (run on the Pi as root):
  - Assign static IP: `sudo ./safe-static-lease.sh 00:d8:61:34:29:8a 192.168.10.50`
  - Remove static IP: `sudo ./safe-static-lease.sh 00:d8:61:34:29:8a ""`

### Notes
- Run this script on the Pi host (not inside a container).
- The script attempts several service control backends (systemctl, service, init.d) to be compatible with different Pi setups.
- The script is intentionally simple and avoids advanced dependencies so it can be used by automated agents or maintainers.
- Keep this folder excluded from any production build/publishing steps.
