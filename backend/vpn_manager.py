"""
VPN management functions for WireGuard
Uses a single wg0 interface and switches configs by rewriting the config file.
Aggressively cleans up old systemd services created by legacy scripts.
"""
import logging
from pathlib import Path
from typing import List, Dict, Any
from fastapi import HTTPException

from config_manager import (
    WIREGUARD_DIR, WG_INTERFACE, WG_CONFIG_FILE, VPN_PROFILES_DIR,
    load_config, save_config
)
from utils import run_command

logger = logging.getLogger("uvicorn")


def set_boot_persistence(enable: bool):
    """
    Enables or disables the systemd service for the current wg0 interface.
    """
    action = "enable" if enable else "disable"
    logger.info(f"Setting boot persistence to: {action}")
    
    # systemctl enable/disable wg-quick@wg0
    success, output = run_command(
        ["systemctl", action, f"wg-quick@{WG_INTERFACE}"], 
        use_sudo=True
    )
    
    if not success:
        logger.error(f"Failed to set boot persistence: {output}")


def cleanup_previous_state():
    """
    Aggressively cleans up ANY WireGuard state left by previous scripts.
    1. Scans /etc/wireguard for all config files.
    2. Disables and Stops their systemd services (fixes the Bash script legacy).
    3. Shuts down any active network interfaces.
    """
    logger.info("Performing aggressive cleanup of previous VPN states...")

    # --- Step 1: Clean up Systemd Services ---
    # The Bash script created services like 'wg-quick@wg-lon-st001'.
    # We must find them and kill them.
    wg_dir = Path(WIREGUARD_DIR)
    if wg_dir.exists():
        for conf_file in wg_dir.glob("*.conf"):
            profile_name = conf_file.stem  # e.g., "wg-lon-st001" or "wg0"
            
            # We want to disable EVERYTHING, even wg0, to ensure a clean slate
            # before we start the specific one we want.
            
            # Disable boot persistence
            run_command(
                ["systemctl", "disable", f"wg-quick@{profile_name}"], 
                use_sudo=True
            )
            
            # Stop the service immediately
            run_command(
                ["systemctl", "stop", f"wg-quick@{profile_name}"], 
                use_sudo=True
            )

    # --- Step 2: Clean up Runtime Interfaces ---
    # Sometimes systemctl stop fails if the service wasn't running, but the 
    # interface might still be up manually.
    success, output = run_command(["wg", "show", "interfaces"], use_sudo=True)
    
    if success and output.strip():
        interfaces = output.strip().split()
        for iface in interfaces:
            logger.info(f"Found active interface: {iface}. Shutting down...")
            
            # Try standard wg-quick down
            down_success, _ = run_command(["wg-quick", "down", iface], use_sudo=True)
            
            # If wg-quick fails (common if config file was deleted/moved), force delete
            if not down_success:
                logger.warning(f"wg-quick down failed for {iface}. Forcing link delete.")
                run_command(["ip", "link", "delete", iface], use_sudo=True)


def list_vpn_configs() -> Dict[str, Any]:
    """List all available VPN profile configurations from profiles directory"""
    configs = []
    config = load_config()
    
    # Check if wg0 is currently active
    success, wg_output = run_command(["wg", "show", WG_INTERFACE], use_sudo=True)
    is_active = success and len(wg_output.strip()) > 0
    
    # List all .conf files in VPN profiles directory
    if VPN_PROFILES_DIR.exists():
        for conf_file in sorted(VPN_PROFILES_DIR.glob("*.conf")):
            profile_name = conf_file.stem
            is_current = config.active_vpn == profile_name
            
            configs.append({
                "name": profile_name,
                "filename": conf_file.name,
                "active": is_active and is_current,  # For frontend compatibility
                "is_current": is_current
            })
    
    return {
        "configs": configs,
        "active": is_active,
        "interface": WG_INTERFACE,
        "kill_switch_enabled": config.kill_switch_enabled
    }


def get_vpn_status() -> Dict[str, Any]:
    """Get current VPN connection status"""
    success, output = run_command(["wg", "show", WG_INTERFACE], use_sudo=True)
    config = load_config()
    
    if success and output.strip():
        return {
            "connected": True,
            "interface": WG_INTERFACE,
            "active_profile": config.active_vpn,
            "details": output
        }
    return {
        "connected": False,
        "interface": WG_INTERFACE,
        "active_profile": None,
        "details": None
    }


def connect_vpn(profile_name: str) -> Dict[str, Any]:
    """
    Connect to a VPN profile.
    Overrides any previous Bash script settings by wiping old services.
    """
    profile_file = VPN_PROFILES_DIR / f"{profile_name}.conf"
    
    if not profile_file.exists():
        raise HTTPException(status_code=404, detail=f"VPN profile '{profile_name}' not found")
    
    config = load_config()
    logger.info(f"Switching to VPN profile: {profile_name}")
    
    # 1. AGGRESSIVE CLEANUP (Overrides Bash script)
    cleanup_previous_state()
    
    # 2. Read Profile
    try:
        profile_content = profile_file.read_text()
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to read profile: {e}")
    
    # 3. Write to /etc/wireguard/wg0.conf
    try:
        temp_file = Path(f"/tmp/{WG_INTERFACE}.conf")
        temp_file.write_text(profile_content)
        
        success, output = run_command(["cp", str(temp_file), str(WG_CONFIG_FILE)], use_sudo=True)
        if not success:
            raise HTTPException(status_code=500, detail=f"Failed to write config: {output}")
        
        run_command(["chmod", "600", str(WG_CONFIG_FILE)], use_sudo=True)
        temp_file.unlink()
        logger.info(f"Wrote profile {profile_name} to {WG_CONFIG_FILE}")
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to write wg0.conf: {e}")
    
    # 4. Start wg0
    success, output = run_command(["wg-quick", "up", WG_INTERFACE], use_sudo=True)
    if not success:
        raise HTTPException(status_code=500, detail=f"Failed to start wg0: {output}")
    
    # 5. Enable Boot Persistence (Only for wg0)
    set_boot_persistence(True)

    # 6. Handle Kill Switch
    if config.kill_switch_enabled:
        from routing import apply_kill_switch
        apply_kill_switch(False)
        logger.info("Kill switch deactivated (VPN connected)")
    
    config.active_vpn = profile_name
    save_config(config)
    
    return {
        "status": "connected",
        "profile": profile_name,
        "interface": WG_INTERFACE,
        "output": output
    }


def disconnect_vpn() -> Dict[str, Any]:
    """Disconnect from VPN"""
    config = load_config()
    
    success, output = run_command(["wg", "show", WG_INTERFACE], use_sudo=True)
    if not success or not output.strip():
        return {"status": "no_vpn_active", "message": "VPN is not currently connected"}
    
    # 1. Stop wg0
    success, output = run_command(["wg-quick", "down", WG_INTERFACE], use_sudo=True)
    if not success:
        logger.error(f"Failed to stop {WG_INTERFACE}: {output}")
        raise HTTPException(status_code=500, detail=f"Failed to stop VPN: {output}")
    
    # 2. Disable Boot Persistence
    set_boot_persistence(False)
    
    old_profile = config.active_vpn
    config.active_vpn = None
    save_config(config)
    
    # If kill switch is enabled, block all traffic
    if config.kill_switch_enabled:
        from routing import apply_kill_switch
        apply_kill_switch(True)
        logger.info("Kill switch engaged - internet blocked")
    
    return {
        "status": "disconnected",
        "message": f"Disconnected from {old_profile or 'VPN'}",
        "kill_switch_active": config.kill_switch_enabled
    }


def toggle_kill_switch(enabled: bool) -> Dict[str, Any]:
    """Toggle the VPN kill switch"""
    config = load_config()
    config.kill_switch_enabled = enabled
    save_config(config)
    
    # Apply kill switch rules
    if enabled and not config.active_vpn:
        # No VPN active, block all traffic
        from routing import apply_kill_switch
        apply_kill_switch(True)
        logger.info("Kill switch enabled - internet blocked (no VPN)")
    elif not enabled:
        # Disable kill switch
        from routing import apply_kill_switch
        apply_kill_switch(False)
        logger.info("Kill switch disabled")
    
    return {
        "status": "success",
        "kill_switch_enabled": enabled,
        "vpn_active": config.active_vpn is not None
    }


def add_vpn_profile(name: str, config_content: str) -> Dict[str, Any]:
    """Add a new VPN profile to the profiles directory"""
    # Sanitize name (no path traversal)
    name = name.replace("/", "").replace("\\", "").replace("..", "")
    if not name:
        raise HTTPException(status_code=400, detail="Invalid profile name")
    
    # Ensure name doesn't already exist
    profile_file = VPN_PROFILES_DIR / f"{name}.conf"
    if profile_file.exists():
        raise HTTPException(status_code=409, detail=f"VPN profile '{name}' already exists")
    
    # Validate config content (basic check for WireGuard format)
    if "[Interface]" not in config_content or "[Peer]" not in config_content:
        raise HTTPException(status_code=400, detail="Invalid WireGuard configuration format")
    
    # Write the profile file
    try:
        profile_file.write_text(config_content)
        logger.info(f"Added new VPN profile: {name}")
        
        return {
            "status": "success",
            "message": f"VPN profile '{name}' added successfully",
            "name": name
        }
    except Exception as e:
        logger.error(f"Failed to add VPN profile: {e}")
        raise HTTPException(status_code=500, detail=str(e))


def delete_vpn_profile(name: str) -> Dict[str, Any]:
    """Delete a VPN profile"""
    profile_file = VPN_PROFILES_DIR / f"{name}.conf"
    
    if not profile_file.exists():
        raise HTTPException(status_code=404, detail=f"VPN profile '{name}' not found")
    
    config = load_config()
    
    # Prevent deleting currently active profile
    if config.active_vpn == name:
        raise HTTPException(
            status_code=400,
            detail=f"Cannot delete active profile '{name}'. Disconnect first."
        )
    
    try:
        profile_file.unlink()
        logger.info(f"Deleted VPN profile: {name}")
        
        return {
            "status": "success",
            "message": f"VPN profile '{name}' deleted successfully"
        }
    except Exception as e:
        logger.error(f"Failed to delete VPN profile: {e}")
        raise HTTPException(status_code=500, detail=str(e))