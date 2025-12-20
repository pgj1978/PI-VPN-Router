"""
Device management functions
"""
import logging
from pathlib import Path
from typing import Dict, Any, List
from fastapi import HTTPException

from models import Device
from config_manager import load_config, save_config
from routing import apply_device_routing

logger = logging.getLogger("uvicorn")


def list_devices() -> Dict[str, List[Dict[str, Any]]]:
    """List all DHCP devices and their bypass status"""
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


def set_device_bypass(mac: str, bypass: bool) -> Dict[str, Any]:
    """Set VPN bypass for a specific device"""
    # URL decode MAC address (handle %3A -> :)
    mac = mac.replace('%3A', ':').replace('%3a', ':')
    
    config = load_config()
    device = next((d for d in config.devices if d.mac.lower() == mac.lower()), None)
    
    if device:
        device.bypass_vpn = bypass
    else:
        devices_response = list_devices()
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


def get_system_info() -> Dict[str, str]:
    """Get system network information"""
    from utils import run_command
    
    success, ifconfig = run_command(["ip", "addr", "show"])
    success2, routes = run_command(["ip", "route", "show"])
    
    return {
        "interfaces": ifconfig if success else "N/A",
        "routes": routes if success2 else "N/A"
    }
