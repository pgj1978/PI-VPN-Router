"""
System information API routes
"""
from fastapi import APIRouter

import device_manager

router = APIRouter()


@router.get("/info")
async def system_info():
    """Get system network information"""
    return device_manager.get_system_info()
