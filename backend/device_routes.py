"""
Device-related API routes
"""
from fastapi import APIRouter

import device_manager

router = APIRouter()


@router.get("")
async def list_devices():
    """List all DHCP devices and their bypass status"""
    return device_manager.list_devices()


@router.post("/{mac}/bypass")
@router.get("/{mac}/bypass")
async def set_device_bypass(mac: str, bypass: bool = False):
    """Set VPN bypass for a specific device"""
    return device_manager.set_device_bypass(mac, bypass)
