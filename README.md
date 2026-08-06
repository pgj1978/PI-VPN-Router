# PiRouter

A Raspberry Pi that routes a LAN through a WireGuard VPN, with a web UI for managing it.
Selected devices and domains can be sent straight out to the internet instead.

```
      devices                      Pi                        internet
    ┌──────────┐        ┌───────────────────────┐
    │ laptop   │        │ eth1  192.168.20.1    │   wg0 ──► VPN provider ──►
    │ phone    │───────►│       DHCP + DNS      │
    │ tv       │        │ eth0 ─────────────────┼───────► upstream router ──►
    └──────────┘        └───────────────────────┘         (bypassed traffic)
```

- **UI** — `http://192.168.20.1:4200`
- **API** — `http://192.168.20.1:51508/api` (bound to LAN and loopback only)

## What it does

| | |
|---|---|
| VPN | Connect, switch and add WireGuard profiles. Reconnects on its own when a tunnel goes silent. |
| Device bypass | Send a chosen device direct to the internet. Keyed on MAC, so it survives DHCP changes and reboots. |
| Domain bypass | Send a chosen hostname direct. Re-resolved on a schedule, so it keeps working when addresses move. |
| Kill switch | Blocks non-bypassed traffic from leaving via the WAN, whether or not the tunnel is up. |
| Logs | Live tail in the UI, including every privileged command the router runs. |
| Diagnostics | Named health checks with the fix for each, covering the failures this router has actually hit. |

## Setup

The Pi needs two network interfaces: one to the upstream router (WAN), one to your devices
(LAN). Beyond that:

```bash
git clone <this repo> && cd PiRouter/deploy
cp .env.example .env
$EDITOR .env                  # interfaces and addressing live here, and nowhere else

sudo ./install.sh --check     # report what it would change, touching nothing
sudo ./install.sh             # do it
docker compose up -d --build
```

`install.sh` is idempotent, so re-run it whenever. It handles only what a container
genuinely cannot: the WireGuard kernel module, `ip_forward`, the LAN interface's static
address, and clearing legacy persisted firewall rules. Everything else runs in Docker.

Drop WireGuard `.conf` files into `deploy/vpn_profiles/`, or paste them into the UI.

### What still has to be true of the host

Being honest about the limits of "just Docker": a container cannot conjure a second network
adapter, enable IP forwarding in the host kernel, or load a kernel module. Those are what
`install.sh` exists for. Everything above that line is containerised, including dnsmasq.

## How it works

The important design decision is that **firewall state is compiled, not mutated**.

```
  config + live facts  ──►  RouterState  ──►  RuleCompiler  ──►  RuleSet
      (intent)             (leases, tunnel,     (pure fn)         (rules)
                            gateway, resolved                        │
                            domains)                                 ▼
                                                              flush + rebuild
                                                             PIROUTER_* chains
```

- PiRouter creates four chains of its own and writes **only** inside them. Docker's chains
  are untouched. `INPUT` is never touched at all, so there is no way to lock yourself out.
- Every apply flushes those chains and rebuilds them from the compiled set. There is no
  incremental add/delete path, which is why rules can no longer accumulate or go stale.
- A reconciler re-checks every 15 seconds and repairs drift, so a DHCP lease change, a VPN
  reconnect or a reboot cannot leave routing quietly wrong.
- `RuleCompiler` is a pure function, so all of this is unit-tested without a Pi.

Run the tests:

```bash
dotnet test
```

## Layout

```
src/PiRouter.Core/     domain, rule compiler, services
src/PiRouter.Api/      controllers, SSE, DI
tests/                 rule compiler and parsing tests
ui/                    Angular 22 + Material
deploy/                compose stack, install.sh, .env
```

## Configuration

Everything lives in `deploy/.env`. Nothing about the network is hardcoded in code, and the
API additionally discovers the LAN address and upstream gateway at runtime, preferring what
it finds over what it was told.

## Troubleshooting

Start at **Diagnostics** in the UI. It checks IP forwarding, both interfaces, the upstream
gateway, DNS resolution, this container's own resolver, the VPN endpoint's DNS, tunnel
handshake age, the real exit address, dnsmasq, and firewall drift — each with the specific
command that fixes it.

`install.sh` backs up firewall state to `/var/backups/pirouter/` before changing anything,
so rolling back is:

```bash
docker compose down
sudo iptables-restore < /var/backups/pirouter/live-rules.v4.<timestamp>
```
