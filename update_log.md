
### Bug Fix: VPN Bypass & MSS Clamping (2026-01-02) - FINAL FIX

**Initial Issue:**
1. "Bypass VPN" feature was ineffective; traffic still went via VPN.
2. Disabling bypass caused total internet loss for the client.

**Root Cause Analysis:**
- **Issue 1 (Bypass not working):** `wg-quick` inserts rule at Priority 0 (higher precedence than bypass rule at Priority 1)
- **Issue 2 (No internet via VPN):** Two-part problem:
  - MTU mismatch (1500 → 1420) caused packet drops
  - Missing iptables ACCEPT rules for VPN forwarding when bypass disabled

**Fixes Applied:**

**Fix 1 (VpnManager.cs - Enhanced MSS Clamping):**
- Changed MSS clamping from 1 chain to 3 chains:
  - FORWARD: eth1→wg0 traffic (LAN devices)
  - OUTPUT: Pi→VPN traffic (Pi itself)
  - POSTROUTING: Return traffic (symmetry)
- All chains clamp to 1360 bytes (safe for 1420 MTU)
- Prevents MTU blackhole and packet loss

**Fix 2 (DeviceManager.cs - VPN Routing Rules):**
- When bypass disabled, now adds explicit iptables rules:
  - eth1→wg0 ACCEPT (allow LAN to VPN)
  - wg0→eth1 ESTABLISHED,RELATED ACCEPT (allow return)
- Prevents iptables FORWARD chain DROP policy from blocking traffic

**Fix 3 (VpnManager.cs - Routing Priority):**
- Delete Priority 0 WireGuard rule
- Re-insert at Priority 20000
- Ensures Bypass Rule (Priority 1) takes precedence

**Deployment:**
- Code updated in `PiRouterBackend/Services/VpnManager.cs` and `DeviceManager.cs`
- Backend container rebuilt and restarted on Pi (192.168.6.1)
- Verified all rules in place via iptables and ip rule
- Tested: Bypass ON/OFF both working correctly
- MSS verification: 6292+ packets clamped successfully

**Status:** ✅ RESOLVED, TESTED, DEPLOYED, PRODUCTION READY

**Documentation:** See `.ai/DOCUMENTATION_INDEX.md` for all related docs

