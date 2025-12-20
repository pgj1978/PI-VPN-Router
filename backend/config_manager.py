"""
Configuration management for PiRouter
"""
import json
from pathlib import Path
from models import RouterConfig

# Configuration paths
WIREGUARD_DIR = Path("/etc/wireguard")
WG_INTERFACE = "wg0"  # Single WireGuard interface name
WG_CONFIG_FILE = WIREGUARD_DIR / f"{WG_INTERFACE}.conf"

# VPN profiles directory (stored separately from active config)
USER_HOME = Path("/home/pgj99")  # Explicit path since service runs as root

if Path("/app").exists():
    VPN_PROFILES_DIR = Path("/app/config/vpn_profiles")
    CONFIG_FILE = Path("/app/config/router_config.json")
else:
    VPN_PROFILES_DIR = USER_HOME / "code/PiRouter/backend/config/vpn_profiles"
    CONFIG_FILE = USER_HOME / "code/PiRouter/backend/config/router_config.json"

BYPASS_TABLE = "100"

# Ensure VPN profiles directory exists
VPN_PROFILES_DIR.mkdir(parents=True, exist_ok=True)


def load_config() -> RouterConfig:
    """Load router configuration from JSON file"""
    if CONFIG_FILE.exists():
        with open(CONFIG_FILE, 'r') as f:
            data = json.load(f)
            return RouterConfig(**data)
    return RouterConfig()


def save_config(config: RouterConfig):
    """Save router configuration to JSON file"""
    CONFIG_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(CONFIG_FILE, 'w') as f:
        json.dump(config.dict(), f, indent=2)
