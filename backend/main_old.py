from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from typing import List, Optional
import subprocess
import os
import json
from pathlib import Path

app = FastAPI(title="PiRouter VPN Manager")

# CORS middleware for Angular frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Configuration
WIREGUARD_DIR = Path("/etc/wireguard")
# Use local config directory if /app doesn't exist (non-Docker)
if Path("/app").exists():
    CONFIG_FILE = Path("/app/config/router_config.json")
else:
    CONFIG_FILE = Path.home() / "code/PiRouter/backend/config/router_config.json"
BYPASS_TABLE = "100"

class WireGuardConfig(BaseModel):
    name: str
    filename: str
    active: bool = False

class Device(BaseModel):
    mac: str
    ip: str
    hostname: Optional[str] = None
    bypass_vpn: bool = False

class DomainBypass(BaseModel):
    domain: str
    enabled: bool = True

class RouterConfig(BaseModel):
    active_vpn: Optional[str] = None
    kill_switch_enabled: bool = False
    devices: List[Device] = []
    domain_bypasses: List[DomainBypass] = []

def load_config() -> RouterConfig:
    if CONFIG_FILE.exists():
        with open(CONFIG_FILE, 'r') as f:
            data = json.load(f)
            return RouterConfig(**data)
    return RouterConfig()

def save_config(config: RouterConfig):
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(CONFIG_FILE, 'w') as f:
        json.dump(config.dict(), f, indent=2)

def run_command(cmd: List[str], use_sudo: bool = False) -> tuple:
    try:
        if use_sudo:
            cmd = ["sudo"] + cmd
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        return True, result.stdout
    except subprocess.CalledProcessError as e:
        return False, e.stderr

@app.get("/")
async def root():
    return {"message": "PiRouter VPN Manager API", "version": "1.0.0"}

@app.get("/api/vpn/configs")
async def list_vpn_configs():
    """List all available WireGuard configurations from /etc/wireguard/"""
    configs = []
    config = load_config()
    
    # Use sudo find to list configs since /etc/wireguard is root-only
    success, output = run_command(["find", "/etc/wireguard", "-maxdepth", "1", "-name", "*.conf", "-type", "f"], use_sudo=True)
    
    if success and output.strip():
        conf_files = output.strip().split('\n')
        
        for conf_path in sorted(conf_files):
            if not conf_path:
                continue
            
            # Extract name from path
            conf_name = Path(conf_path).stem
            conf_filename = Path(conf_path).name
            
            # Check if this VPN is currently active
            success2, wg_output = run_command(["wg", "show", conf_name], use_sudo=True)
            is_active = success2 and len(wg_output.strip()) > 0
            
            configs.append({
                "name": conf_name,
                "filename": conf_filename,
                "active": is_active,
                "is_current": config.active_vpn == conf_name
            })
    
    # Remove duplicates by name (keep first occurrence)
    seen = set()
    unique_configs = []
    for cfg in configs:
        if cfg["name"] not in seen:
            seen.add(cfg["name"])
            unique_configs.append(cfg)
    
    return {
        "configs": unique_configs,
        "kill_switch_enabled": config.kill_switch_enabled
    }

@app.get("/api/vpn/status")
async def vpn_status():
    success, output = run_command(["wg", "show"], use_sudo=True)
    if success and output.strip():
        lines = output.strip().split('\n')
        interface = lines[0].split(':')[0].replace('interface', '').strip() if lines else None
        return {
            "connected": True,
            "interface": interface,
            "details": output
        }
    return {"connected": False, "interface": None, "details": None}

@app.post("/api/vpn/connect/{config_name}")
async def connect_vpn(config_name: str):
    import logging
    logger = logging.getLogger("uvicorn")
    
    config_file = WIREGUARD_DIR / f"{config_name}.conf"
    
    if not config_file.exists():
        raise HTTPException(status_code=404, detail=f"Configuration {config_name} not found")
    
    config = load_config()
    
    # Stop ALL running VPN interfaces to prevent conflicts
    # Get list of all running WireGuard interfaces
    success, wg_output = run_command(["wg", "show", "interfaces"], use_sudo=True)
    if success and wg_output.strip():
        running_interfaces = wg_output.strip().split()
        for iface in running_interfaces:
            logger.info(f"Stopping existing VPN interface: {iface}")
            run_command(["wg-quick", "down", iface], use_sudo=True)
    
    # Start new VPN
    success, output = run_command(["wg-quick", "up", config_name], use_sudo=True)
    
    if not success:
        raise HTTPException(status_code=500, detail=f"Failed to connect: {output}")
    
    # Note: wg-quick handles routing via PostUp scripts in the .conf file
    # No need to manually setup routing - it breaks the fwmark-based routing
    
    # Disable kill switch since VPN is now active
    if config.kill_switch_enabled:
        apply_kill_switch(False)
        logger.info("Kill switch deactivated (VPN connected)")
    
    config.active_vpn = config_name
    save_config(config)
    
    return {"status": "connected", "vpn": config_name, "output": output}

# Note: The new disconnect_vpn endpoint with kill switch support is defined below around line 302

# Old disconnect endpoint removed - see improved version with kill switch support around line 302


@app.get("/api/devices")
async def list_devices():
    config = load_config()
    dhcp_leases = []
    leases_file = Path("/var/lib/misc/dnsmasq.leases")
    
    if leases_file.exists():
        with open(leases_file, 'r') as f:
            for line in f:
                parts = line.strip().split()
                if len(parts) >= 4:
                    dhcp_leases.append({
                        "mac": parts[1],
                        "ip": parts[2],
                        "hostname": parts[3] if parts[3] != "*" else None
                    })
    
    devices = []
    for lease in dhcp_leases:
        saved_device = next((d for d in config.devices if d.mac.lower() == lease["mac"].lower()), None)
        devices.append({
            **lease,
            "bypass_vpn": saved_device.bypass_vpn if saved_device else False
        })
    
    return {"devices": devices}

@app.post("/api/devices/{mac}/bypass")
@app.get("/api/devices/{mac}/bypass")
async def set_device_bypass(mac: str, bypass: bool = False):
    # URL decode MAC address (handle %3A -> :)
    mac = mac.replace('%3A', ':').replace('%3a', ':')
    
    config = load_config()
    device = next((d for d in config.devices if d.mac.lower() == mac.lower()), None)
    
    if device:
        device.bypass_vpn = bypass
    else:
        devices_response = await list_devices()
        device_info = next((d for d in devices_response["devices"] if d["mac"].lower() == mac.lower()), None)
        if device_info:
            config.devices.append(Device(
                mac=mac,
                ip=device_info["ip"],
                hostname=device_info.get("hostname"),
                bypass_vpn=bypass
            ))
        else:
            raise HTTPException(status_code=404, detail=f"Device with MAC {mac} not found")
    
    save_config(config)
    apply_device_routing(mac, bypass)
    
    return {"status": "updated", "mac": mac, "bypass_vpn": bypass}

def setup_vpn_routing():
    success, output = run_command(["wg", "show", "interfaces"], use_sudo=True)
    if not success or not output.strip():
        return
    
    wg_interface = output.strip().split()[0]
    run_command(["ip", "route", "add", "default", "dev", wg_interface, "table", "main"], use_sudo=True)
    
    config = load_config()
    for device in config.devices:
        if device.bypass_vpn:
            apply_device_routing(device.mac, True)

def restore_default_routing():
    run_command(["ip", "route", "del", "default", "table", "main"], use_sudo=True)
    run_command(["ip", "route", "add", "default", "via", "192.168.0.1", "dev", "eth1"], use_sudo=True)

def apply_device_routing(mac: str, bypass: bool):
    import logging
    logger = logging.getLogger("uvicorn")
    
    logger.info(f"apply_device_routing called: mac={mac}, bypass={bypass}")
    
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
    
    if bypass:
        logger.info(f"Enabling bypass for {ip}")
        # Remove existing rule first (in case it exists) - must include priority to delete correctly
        # Ignore errors as rule may not exist
        run_command(["ip", "rule", "del", "from", ip, "table", BYPASS_TABLE, "priority", "100"], use_sudo=True)
        
        # Add routing rule for this device to bypass table with priority 100 (before VPN rule at 32762)
        success, output = run_command(["ip", "rule", "add", "from", ip, "table", BYPASS_TABLE, "priority", "100"], use_sudo=True)
        if not success:
            logger.error(f"Failed to add bypass rule: {output}")
        
        # Ensure bypass table has default route via normal gateway (remove old first to avoid duplicates)
        run_command(["ip", "route", "del", "default", "table", BYPASS_TABLE], use_sudo=True)
        
        success, output = run_command(["ip", "route", "add", "default", "via", "192.168.5.1", "dev", "eth0", "table", BYPASS_TABLE], use_sudo=True)
        if not success:
            logger.error(f"Failed to add bypass route: {output}")
    else:
        logger.info(f"Disabling bypass for {ip}")
        # Remove bypass rule - must include priority to delete correctly
        success, output = run_command(["ip", "rule", "del", "from", ip, "table", BYPASS_TABLE, "priority", "100"], use_sudo=True)
        if not success:
            logger.warning(f"Failed to delete bypass rule (may not exist): {output}")
        
        # Also clear the bypass table route (in case it's the last device)
        run_command(["ip", "route", "del", "default", "table", BYPASS_TABLE], use_sudo=True)
    
    # Flush routing cache to force immediate update
    logger.info("Flushing routing cache")
    run_command(["ip", "route", "flush", "cache"], use_sudo=True)
    
    # Kill existing connections from this IP to force reconnection with new routes
    logger.info(f"Dropping connection tracking for {ip}")
    success, output = run_command(["conntrack", "-D", "-s", ip], use_sudo=True)
    if success:
        logger.info(f"Conntrack deleted entries for {ip}")
    
    logger.info(f"apply_device_routing completed for {ip}")

@app.get("/api/system/info")
async def system_info():
    success, ifconfig = run_command(["ip", "addr", "show"])
    success2, routes = run_command(["ip", "route", "show"])
    
    return {
        "interfaces": ifconfig if success else "N/A",
        "routes": routes if success2 else "N/A"
    }

@app.post("/api/vpn/disconnect")
async def disconnect_vpn():
    """Disconnect from VPN without killing internet (unless kill switch is on)"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    config = load_config()
    
    if not config.active_vpn:
        return {"status": "no_vpn_active", "message": "No VPN is currently connected"}
    
    # Stop the active VPN
    success, output = run_command(["wg-quick", "down", config.active_vpn], use_sudo=True)
    if not success:
        logger.error(f"Failed to stop VPN {config.active_vpn}: {output}")
        raise HTTPException(status_code=500, detail=f"Failed to stop VPN: {output}")
    
    old_vpn = config.active_vpn
    config.active_vpn = None
    save_config(config)
    
    # If kill switch is enabled, block all traffic
    if config.kill_switch_enabled:
        apply_kill_switch(True)
        logger.info("Kill switch engaged - internet blocked")
    
    return {
        "status": "disconnected",
        "message": f"Disconnected from {old_vpn}",
        "kill_switch_active": config.kill_switch_enabled
    }

@app.post("/api/vpn/kill-switch")
async def toggle_kill_switch(enabled: bool = False):
    """Toggle the VPN kill switch"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    config = load_config()
    config.kill_switch_enabled = enabled
    save_config(config)
    
    # Apply kill switch rules
    if enabled and not config.active_vpn:
        # No VPN active, block all traffic
        apply_kill_switch(True)
        logger.info("Kill switch enabled - internet blocked (no VPN)")
    elif not enabled:
        # Disable kill switch
        apply_kill_switch(False)
        logger.info("Kill switch disabled")
    
    return {
        "status": "success",
        "kill_switch_enabled": enabled,
        "vpn_active": config.active_vpn is not None
    }

def apply_kill_switch(block: bool):
    """Apply or remove kill switch iptables rules"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    if block:
        # Block all outgoing traffic except:
        # - Local network (192.168.0.0/16)
        # - DNS (for VPN resolution)
        # - VPN connection itself
        
        # Flush existing rules in the FORWARD chain for kill switch
        run_command(["iptables", "-D", "FORWARD", "-m", "comment", "--comment", "KILLSWITCH", "-j", "REJECT"], use_sudo=True)
        
        # Block forwarding (from LAN to WAN) unless going through VPN
        run_command(["iptables", "-I", "FORWARD", "1", "-m", "comment", "--comment", "KILLSWITCH", "-j", "REJECT"], use_sudo=True)
        
        logger.info("Kill switch activated - all traffic blocked")
    else:
        # Remove kill switch rules
        run_command(["iptables", "-D", "FORWARD", "-m", "comment", "--comment", "KILLSWITCH", "-j", "REJECT"], use_sudo=True)
        logger.info("Kill switch deactivated")

@app.post("/api/vpn/add")
async def add_vpn_config(name: str, config_content: str):
    """Add a new WireGuard VPN configuration"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    # Sanitize name (no path traversal)
    name = name.replace("/", "").replace("\\", "").replace("..", "")
    if not name:
        raise HTTPException(status_code=400, detail="Invalid VPN name")
    
    # Ensure name doesn't already exist
    config_file = WIREGUARD_DIR / f"{name}.conf"
    if config_file.exists():
        raise HTTPException(status_code=409, detail=f"VPN config '{name}' already exists")
    
    # Validate config content (basic check for WireGuard format)
    if "[Interface]" not in config_content or "[Peer]" not in config_content:
        raise HTTPException(status_code=400, detail="Invalid WireGuard configuration format")
    
    # Write the config file
    try:
        # Write to temp file first
        temp_file = Path(f"/tmp/{name}.conf")
        temp_file.write_text(config_content)
        
        # Move to wireguard directory with sudo
        success, output = run_command(["cp", str(temp_file), str(config_file)], use_sudo=True)
        if not success:
            raise HTTPException(status_code=500, detail=f"Failed to save config: {output}")
        
        # Set permissions
        run_command(["chmod", "600", str(config_file)], use_sudo=True)
        
        # Clean up temp file
        temp_file.unlink()
        
        logger.info(f"Added new VPN config: {name}")
        
        return {
            "status": "success",
            "message": f"VPN configuration '{name}' added successfully",
            "name": name
        }
    except Exception as e:
        logger.error(f"Failed to add VPN config: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/api/domains/bypass")
async def list_domain_bypasses():
    """List all domains configured to bypass VPN"""
    config = load_config()
    return {"domains": [d.dict() for d in config.domain_bypasses]}

@app.post("/api/domains/bypass")
async def add_domain_bypass(domain: str):
    """Add a domain to bypass VPN (split tunneling)"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    config = load_config()
    
    # Check if domain already exists
    if any(d.domain == domain for d in config.domain_bypasses):
        raise HTTPException(status_code=409, detail=f"Domain '{domain}' already in bypass list")
    
    # Add domain
    config.domain_bypasses.append(DomainBypass(domain=domain, enabled=True))
    save_config(config)
    
    # Apply DNS-based routing for this domain
    apply_domain_bypass(domain, True)
    
    logger.info(f"Added domain bypass: {domain}")
    
    return {
        "status": "success",
        "message": f"Domain '{domain}' added to bypass list",
        "domain": domain
    }

@app.delete("/api/domains/bypass/{domain}")
async def remove_domain_bypass(domain: str):
    """Remove a domain from bypass list"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    config = load_config()
    
    # Find and remove domain
    original_len = len(config.domain_bypasses)
    config.domain_bypasses = [d for d in config.domain_bypasses if d.domain != domain]
    
    if len(config.domain_bypasses) == original_len:
        raise HTTPException(status_code=404, detail=f"Domain '{domain}' not found in bypass list")
    
    save_config(config)
    
    # Remove DNS-based routing for this domain
    apply_domain_bypass(domain, False)
    
    logger.info(f"Removed domain bypass: {domain}")
    
    return {
        "status": "success",
        "message": f"Domain '{domain}' removed from bypass list"
    }

def apply_domain_bypass(domain: str, enable: bool):
    """Apply DNS-based routing to bypass VPN for specific domain"""
    import logging
    logger = logging.getLogger("uvicorn")
    
    # For domain-based bypass, we need to:
    # 1. Resolve domain to IP(s)
    # 2. Add routing rules for those IPs to bypass table
    
    if enable:
        # Resolve domain to IPs
        success, output = run_command(["host", domain])
        if not success:
            logger.warning(f"Could not resolve domain {domain}")
            return
        
        # Parse IPs from host output
        ips = []
        for line in output.split('\n'):
            if "has address" in line:
                parts = line.split()
                if len(parts) >= 4:
                    ips.append(parts[-1])
        
        # Add routing rules for each IP
        for ip in ips:
            logger.info(f"Adding bypass route for {domain} ({ip})")
            # Remove existing rule first
            run_command(["ip", "rule", "del", "to", ip, "table", BYPASS_TABLE], use_sudo=True)
            # Add rule
            run_command(["ip", "rule", "add", "to", ip, "table", BYPASS_TABLE, "priority", "100"], use_sudo=True)
        
        # Ensure bypass table has route
        run_command(["ip", "route", "del", "default", "table", BYPASS_TABLE], use_sudo=True)
        run_command(["ip", "route", "add", "default", "via", "192.168.5.1", "dev", "eth0", "table", BYPASS_TABLE], use_sudo=True)
        
        # Flush cache
        run_command(["ip", "route", "flush", "cache"], use_sudo=True)
    else:
        # Remove routing rules
        # We'll need to resolve again to get IPs
        success, output = run_command(["host", domain])
        if success:
            for line in output.split('\n'):
                if "has address" in line:
                    parts = line.split()
                    if len(parts) >= 4:
                        ip = parts[-1]
                        logger.info(f"Removing bypass route for {domain} ({ip})")
                        run_command(["ip", "rule", "del", "to", ip, "table", BYPASS_TABLE], use_sudo=True)
        
        run_command(["ip", "route", "flush", "cache"], use_sudo=True)

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=51507)
