"""
Data models for PiRouter VPN Manager
"""
from pydantic import BaseModel
from typing import List, Optional


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
