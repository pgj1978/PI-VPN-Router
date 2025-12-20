# PiRouter Backend - Code Structure

## Overview

The backend has been refactored into a modular structure for better maintainability and readability.

## File Structure

```
backend/
├── main.py                 # Main FastAPI application and route registration
├── models.py               # Pydantic data models
├── config_manager.py       # Configuration file management
├── utils.py                # System command utilities
├── vpn_manager.py          # VPN connection logic
├── routing.py              # Network routing and iptables rules
├── device_manager.py       # Device listing and bypass management
├── domain_manager.py       # Domain bypass management
├── vpn_routes.py           # VPN API endpoints
├── device_routes.py        # Device API endpoints
├── domain_routes.py        # Domain bypass API endpoints
├── system_routes.py        # System info API endpoints
├── requirements.txt        # Python dependencies
└── config/
    └── router_config.json  # Runtime configuration
```

## Module Descriptions

### Core Modules

#### `main.py`
- FastAPI application initialization
- CORS middleware configuration
- Route registration
- Entry point for the application

#### `models.py`
- Pydantic data models:
  - `WireGuardConfig` - VPN configuration representation
  - `Device` - Network device with bypass settings
  - `DomainBypass` - Domain bypass configuration
  - `RouterConfig` - Main router configuration

#### `config_manager.py`
- Configuration file I/O
- `load_config()` - Load router configuration from JSON
- `save_config()` - Save router configuration to JSON
- Path management (Docker vs native)

#### `utils.py`
- System command execution
- `run_command()` - Execute shell commands with sudo support
- Error handling and logging

### Business Logic Modules

#### `vpn_manager.py`
VPN connection management:
- `list_vpn_configs()` - List available WireGuard configs
- `get_vpn_status()` - Get current VPN status
- `connect_vpn()` - Connect to a VPN
- `disconnect_vpn()` - Disconnect from VPN
- `toggle_kill_switch()` - Enable/disable kill switch
- `add_vpn_config()` - Add new VPN configuration

#### `routing.py`
Network routing and iptables:
- `apply_kill_switch()` - Apply/remove kill switch rules
- `apply_device_routing()` - Set up per-device bypass routes
- `apply_domain_bypass()` - Set up domain-specific routing

#### `device_manager.py`
Device management:
- `list_devices()` - List DHCP devices from dnsmasq
- `set_device_bypass()` - Configure device VPN bypass
- `get_system_info()` - Get network interface info

#### `domain_manager.py`
Domain bypass management:
- `list_domain_bypasses()` - List bypassed domains
- `add_domain_bypass()` - Add domain to bypass list
- `remove_domain_bypass()` - Remove domain from bypass list

### API Route Modules

#### `vpn_routes.py`
VPN API endpoints:
- `GET /api/vpn/configs` - List VPN configurations
- `GET /api/vpn/status` - Get VPN status
- `POST /api/vpn/connect/{name}` - Connect to VPN
- `POST /api/vpn/disconnect` - Disconnect VPN
- `POST /api/vpn/kill-switch` - Toggle kill switch
- `POST /api/vpn/add` - Add VPN configuration

#### `device_routes.py`
Device API endpoints:
- `GET /api/devices` - List devices
- `POST /api/devices/{mac}/bypass` - Set device bypass

#### `domain_routes.py`
Domain bypass API endpoints:
- `GET /api/domains/bypass` - List bypassed domains
- `POST /api/domains/bypass` - Add domain bypass
- `DELETE /api/domains/bypass/{domain}` - Remove domain bypass

#### `system_routes.py`
System information endpoints:
- `GET /api/system/info` - Get network information

## Data Flow

```
HTTP Request
    ↓
main.py (FastAPI)
    ↓
*_routes.py (Route handlers)
    ↓
*_manager.py (Business logic)
    ↓
├─→ config_manager.py (Config I/O)
├─→ utils.py (System commands)
└─→ routing.py (Network routing)
```

## Adding New Features

### 1. Add a new API endpoint

**Create route in appropriate `*_routes.py`:**
```python
@router.get("/new-endpoint")
async def new_endpoint():
    return some_manager.new_function()
```

**Add business logic in `*_manager.py`:**
```python
def new_function():
    # Your logic here
    return {"result": "success"}
```

### 2. Add a new data model

**Add to `models.py`:**
```python
class NewModel(BaseModel):
    field1: str
    field2: int
```

### 3. Add system command

**Use `utils.run_command()`:**
```python
from utils import run_command

success, output = run_command(["your", "command"], use_sudo=True)
```

## Testing Modules

You can test individual modules:

```python
# Test config management
from config_manager import load_config, save_config
config = load_config()
print(config.dict())

# Test VPN manager
from vpn_manager import list_vpn_configs
configs = list_vpn_configs()
print(configs)
```

## Benefits of New Structure

1. **Separation of Concerns**
   - Web layer (routes) separate from business logic
   - Business logic separate from system commands
   - Models separate from everything

2. **Easier Testing**
   - Mock individual modules
   - Test business logic without web server
   - Unit test each component

3. **Better Readability**
   - Clear module boundaries
   - Easy to find specific functionality
   - Self-documenting structure

4. **Maintainability**
   - Changes isolated to specific modules
   - Less risk of breaking unrelated code
   - Easier to add new features

5. **Scalability**
   - Easy to add new endpoints
   - Can split into microservices later
   - Clear dependency graph

## Logging

All modules use Python's logging:

```python
import logging
logger = logging.getLogger("uvicorn")

logger.info("Info message")
logger.warning("Warning message")
logger.error("Error message")
```

View logs with:
```bash
sudo journalctl -u pirouter-backend -f
```

## Dependencies Between Modules

```
main.py
  ├─→ *_routes.py
       ├─→ *_manager.py
            ├─→ config_manager.py
            ├─→ utils.py
            ├─→ routing.py
            └─→ models.py
```

**No circular dependencies** - each module only imports from lower levels.
