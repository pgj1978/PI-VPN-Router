"""
Domain bypass API routes
"""
from fastapi import APIRouter

import domain_manager

router = APIRouter()


@router.get("/bypass")
async def list_domain_bypasses():
    """List all domains configured to bypass VPN"""
    return domain_manager.list_domain_bypasses()


@router.post("/bypass")
async def add_domain_bypass(domain: str):
    """Add a domain to bypass VPN (split tunneling)"""
    return domain_manager.add_domain_bypass(domain)


@router.delete("/bypass/{domain}")
async def remove_domain_bypass(domain: str):
    """Remove a domain from bypass list"""
    return domain_manager.remove_domain_bypass(domain)
