
## 2026-01-02: VPN Bypass Fix & MSS Clamping

### Issue 1: Bypass VPN not working
Devices set to "Bypass VPN" were still being routed through the VPN tunnel.
**Diagnosis:** `wg-quick` creates a routing rule with Priority 0 (highest) that captures all traffic not matching its own `fwmark`. The "Bypass" rule (Priority 1) was being overridden by this default WireGuard rule.
**Resolution:** Modified `VpnManager.cs` (`ApplyRoutingExceptions`) to shift the WireGuard capture rule from Priority 0 to Priority 20000. This allows the Bypass rule (Priority 1) to take precedence for marked traffic.

### Issue 2: No Internet when VPN is Active (Bypass Disabled)
After fixing the bypass priority, devices routed through the VPN (Bypass disabled) lost internet connectivity, while the Pi itself could still connect.
**Diagnosis:** MTU/MSS mismatch. The standard Ethernet MTU (1500) caused packets to be dropped when encapsulated in the WireGuard tunnel (MTU 1420), creating a "black hole" for TCP connections like HTTPS.
**Resolution:** Added explicit TCP MSS clamping to `VpnManager.cs`. The rule `iptables -t mangle -I FORWARD -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --set-mss 1360` ensures TCP packets fit safely within the tunnel.
**Outcome:** Both "Bypass VPN" and "VPN-routed" modes work correctly.
