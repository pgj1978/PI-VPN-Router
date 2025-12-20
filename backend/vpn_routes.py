"""
VPN-related API routes
"""
from fastapi import APIRouter
from pydantic import BaseModel

import vpn_manager

router = APIRouter()


class ConnectRequest(BaseModel):
    profile_name: str


class KillSwitchRequest(BaseModel):
    enabled: bool


class AddVPNProfileRequest(BaseModel):
    name: str
    config_content: str


@router.get("/profiles")
async def list_vpn_profiles():
    """List all available VPN profiles"""
    return vpn_manager.list_vpn_configs()


@router.get("/configs")
async def list_vpn_configs_compat():
    """
    Legacy endpoint for backward compatibility with frontend
    Redirects to /profiles endpoint
    """
    return vpn_manager.list_vpn_configs()


@router.get("/status")
async def vpn_status():
    """Get current VPN connection status"""
    return vpn_manager.get_vpn_status()


@router.post("/connect/{profile_name}")
async def connect_vpn(profile_name: str):
    """
    Connect to a VPN profile
    This will write the profile config to /etc/wireguard/wg0.conf and restart wg0
    """
    return vpn_manager.connect_vpn(profile_name)


@router.post("/disconnect")
async def disconnect_vpn():
    """Disconnect from current VPN (stop wg0)"""
    return vpn_manager.disconnect_vpn()


@router.post("/kill-switch")
async def toggle_kill_switch(enabled: bool = False):
    """Toggle the VPN kill switch"""
    return vpn_manager.toggle_kill_switch(enabled)


@router.post("/profile")
async def add_vpn_profile(name: str, config_content: str):
    """Add a new VPN profile"""
    return vpn_manager.add_vpn_profile(name, config_content)


@router.delete("/profile/{name}")
async def delete_vpn_profile(name: str):
    """Delete a VPN profile"""
    return vpn_manager.delete_vpn_profile(name)
