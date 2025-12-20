# Backend Module Quick Reference

## Need to...

### Add a new VPN feature?
→ Edit `vpn_manager.py` (business logic)  
→ Add route in `vpn_routes.py` (API endpoint)

### Change routing behavior?
→ Edit `routing.py`

### Add a new device feature?
→ Edit `device_manager.py` (business logic)  
→ Add route in `device_routes.py` (API endpoint)

### Add a new data model?
→ Edit `models.py`

### Change config file location?
→ Edit `config_manager.py`

### Add new system command?
→ Use `utils.run_command()`

### Add logging?
```python
import logging
logger = logging.getLogger("uvicorn")
logger.info("message")
```

### Test a module?
```python
# Test locally without running server
from vpn_manager import list_vpn_configs
configs = list_vpn_configs()
```

## File Sizes
```
main.py           52 lines   ⭐ Entry point
vpn_manager.py   237 lines   🔌 VPN logic
routing.py       195 lines   🌐 Routing
device_manager.py 93 lines   📱 Devices
domain_manager.py 71 lines   🌍 Domains
vpn_routes.py     59 lines   📡 VPN API
config_manager.py 35 lines   ⚙️  Config
models.py         30 lines   📦 Data models
utils.py          30 lines   🔧 Commands
domain_routes.py  27 lines   📡 Domain API
device_routes.py  20 lines   📡 Device API
system_routes.py  13 lines   📡 System API
```

## Import Graph
```
main.py
  ├─→ vpn_routes.py → vpn_manager.py ┐
  ├─→ device_routes.py → device_manager.py ┤
  ├─→ domain_routes.py → domain_manager.py ┤
  └─→ system_routes.py → device_manager.py ┘
                              ↓
                     ┌────────┴────────┐
                     ↓                 ↓
              config_manager.py    routing.py
                     ↓                 ↓
                  models.py        utils.py
```

## Deploy Changes
```powershell
# Edit files in D:\PiRouter\backend\
# Then deploy:
.\deploy-native.ps1
```

## View Logs
```bash
ssh pgj99@192.168.10.1
sudo journalctl -u pirouter-backend -f
```

## Restart Service
```bash
ssh pgj99@192.168.10.1
sudo systemctl restart pirouter-backend
```

## API Docs
http://192.168.10.1:51507/docs
