"""
Domain bypass management functions
"""
import logging
from typing import Dict, Any, List
from fastapi import HTTPException

from models import DomainBypass
from config_manager import load_config, save_config
from routing import apply_domain_bypass

logger = logging.getLogger("uvicorn")


def list_domain_bypasses() -> Dict[str, List[Dict[str, Any]]]:
    """List all domains configured to bypass VPN"""
    config = load_config()
    return {"domains": [d.dict() for d in config.domain_bypasses]}


def add_domain_bypass(domain: str) -> Dict[str, Any]:
    """Add a domain to bypass VPN (split tunneling)"""
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


def remove_domain_bypass(domain: str) -> Dict[str, Any]:
    """Remove a domain from bypass list"""
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
