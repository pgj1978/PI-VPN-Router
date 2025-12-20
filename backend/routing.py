"""
Network routing and iptables management
Updates: Uses FWMark (Mangle) for robust VPN bypassing
"""
import logging
from typing import List
from utils import run_command
from config_manager import BYPASS_TABLE

logger = logging.getLogger("uvicorn")

# Configuration
WAN_IFACE = "eth0"
LAN_IFACE = "eth1"
GATEWAY_IP = "192.168.5.1"  # Your ISP Modem IP

def apply_kill_switch(block: bool):
    """Apply or remove kill switch iptables rules"""
    if block:
        # Block all outgoing traffic except:
        # - Local network (192.168.0.0/16)
        # - DNS (for VPN resolution)
        # - VPN connection itself
        
        # Flush existing rules in the FORWARD chain for kill switch
        run_command(
            ["iptables", "-D", "FORWARD", "-m", "comment", "--comment", "KILLSWITCH", "-j", "REJECT"],
            use_sudo=True
        )
        
        # Block forwarding (from LAN to WAN) unless going through VPN
        run_command(
            ["iptables", "-I", "FORWARD", "1", "-m", "comment", "--comment", "KILLSWITCH", "-j", "REJECT"],
            use_sudo=True
        )
        
        logger.info("Kill switch activated - all traffic blocked")
    else:
        # Remove kill switch rules
        run_command(
            ["iptables", "-D", "FORWARD", "-m", "comment", "--comment", "KILLSWITCH", "-j", "REJECT"],
            use_sudo=True
        )
        logger.info("Kill switch deactivated")


def ensure_bypass_infrastructure():
    """
    Ensures the underlying routing table and NAT rules exist for the bypass (Table 200).
    This is idempotent (safe to run multiple times).
    """
    # 1. Ensure Table 200 has a default route to the ISP
    # We use 'ip route replace' to avoid errors if it exists
    run_command(
        ["ip", "route", "replace", "default", "via", GATEWAY_IP, "dev", WAN_IFACE, "table", BYPASS_TABLE],
        use_sudo=True
    )

    # 2. Ensure the Kernel looks for the FWMark 200
    # Check if rule exists first to avoid duplicate pile-up
    success, output = run_command(["ip", "rule", "show"], use_sudo=True)
    if f"fwmark 0x{int(BYPASS_TABLE):x} lookup {BYPASS_TABLE}" not in output:
         run_command(
            ["ip", "rule", "add", "fwmark", BYPASS_TABLE, "lookup", BYPASS_TABLE, "priority", "1"],
            use_sudo=True
        )

    # 3. Ensure NAT is enabled for marked packets leaving eth0
    # We delete first to ensure we don't duplicate, then add
    run_command(
        ["iptables", "-t", "nat", "-D", "POSTROUTING", "-o", WAN_IFACE, "-m", "mark", "--mark", BYPASS_TABLE, "-j", "MASQUERADE"],
        use_sudo=True
    )
    run_command(
        ["iptables", "-t", "nat", "-A", "POSTROUTING", "-o", WAN_IFACE, "-m", "mark", "--mark", BYPASS_TABLE, "-j", "MASQUERADE"],
        use_sudo=True
    )


def apply_device_routing(mac: str, bypass: bool):
    """
    Apply routing rules for device bypass using IPTables Mangle (FWMark).
    """
    logger.info(f"apply_device_routing called: mac={mac}, bypass={bypass}")
    
    # 1. Get IP from ARP
    success, output = run_command(["arp", "-n"], use_sudo=True)
    if not success:
        logger.error(f"Failed to get ARP table: {output}")
        return
    
    ip = None
    for line in output.split('\n'):
        if mac.lower() in line.lower():
            parts = line.split()
            if len(parts) > 0:
                ip = parts[0]
                logger.info(f"Found IP {ip} for MAC {mac}")
                break
    
    if not ip:
        logger.warning(f"No IP found for MAC {mac} in ARP table")
        return
    
    # 2. Apply or Remove Rules
    if bypass:
        logger.info(f"Enabling bypass for {ip} (Marking packets with {BYPASS_TABLE})")
        
        # A. Make sure the exit door (Table 200) is ready
        ensure_bypass_infrastructure()

        # B. Clean up old rules for this IP to avoid duplicates
        # Delete Mangle
        run_command(["iptables", "-t", "mangle", "-D", "PREROUTING", "-i", LAN_IFACE, "-s", ip, "-j", "MARK", "--set-mark", BYPASS_TABLE], use_sudo=True)
        # Delete Forwarding
        run_command(["iptables", "-D", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-s", ip, "-j", "ACCEPT"], use_sudo=True)
        run_command(["iptables", "-D", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-d", ip, "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"], use_sudo=True)

        # C. Add New Rules
        # 1. Mark the packets coming from this device
        run_command(
            ["iptables", "-t", "mangle", "-A", "PREROUTING", "-i", LAN_IFACE, "-s", ip, "-j", "MARK", "--set-mark", BYPASS_TABLE],
            use_sudo=True
        )
        
        # 2. Allow Traffic Forwarding (Explicitly allow this IP to go out eth0)
        run_command(["iptables", "-A", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-s", ip, "-j", "ACCEPT"], use_sudo=True)
        run_command(["iptables", "-A", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-d", ip, "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"], use_sudo=True)

    else:
        logger.info(f"Disabling bypass for {ip}")
        
        # Remove Mangle Rule (Stop marking packets)
        run_command(
            ["iptables", "-t", "mangle", "-D", "PREROUTING", "-i", LAN_IFACE, "-s", ip, "-j", "MARK", "--set-mark", BYPASS_TABLE],
            use_sudo=True
        )
        
        # Remove Forwarding Exceptions
        run_command(["iptables", "-D", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-s", ip, "-j", "ACCEPT"], use_sudo=True)
        run_command(["iptables", "-D", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-d", ip, "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"], use_sudo=True)

    # Flush routing cache
    run_command(["ip", "route", "flush", "cache"], use_sudo=True)
    
    # Kill connections to force immediate switch
    logger.info(f"Dropping connection tracking for {ip}")
    run_command(["conntrack", "-D", "-s", ip], use_sudo=True)
    
    logger.info(f"apply_device_routing completed for {ip}")


def apply_domain_bypass(domain: str, enable: bool):
    """Apply DNS-based routing to bypass VPN for specific domain using Mangle"""
    
    if enable:
        # Resolve domain to IPs
        success, output = run_command(["host", domain])
        if not success:
            logger.warning(f"Could not resolve domain {domain}")
            return
        
        # Parse IPs
        ips = []
        for line in output.split('\n'):
            if "has address" in line:
                parts = line.split()
                if len(parts) >= 4:
                    ips.append(parts[-1])
        
        if ips:
            ensure_bypass_infrastructure()

        # Add marking rules for destination IPs
        for ip in ips:
            logger.info(f"Adding bypass mark for domain {domain} ({ip})")
            
            # Delete old rule first
            run_command(["iptables", "-t", "mangle", "-D", "PREROUTING", "-i", LAN_IFACE, "-d", ip, "-j", "MARK", "--set-mark", BYPASS_TABLE], use_sudo=True)
            
            # Add new rule
            run_command(
                ["iptables", "-t", "mangle", "-A", "PREROUTING", "-i", LAN_IFACE, "-d", ip, "-j", "MARK", "--set-mark", BYPASS_TABLE],
                use_sudo=True
            )
            
        run_command(["ip", "route", "flush", "cache"], use_sudo=True)

    else:
        # Remove rules
        success, output = run_command(["host", domain])
        if success:
            for line in output.split('\n'):
                if "has address" in line:
                    parts = line.split()
                    if len(parts) >= 4:
                        ip = parts[-1]
                        logger.info(f"Removing bypass mark for {domain} ({ip})")
                        run_command(
                            ["iptables", "-t", "mangle", "-D", "PREROUTING", "-i", LAN_IFACE, "-d", ip, "-j", "MARK", "--set-mark", BYPASS_TABLE],
                            use_sudo=True
                        )
        
        run_command(["ip", "route", "flush", "cache"], use_sudo=True)