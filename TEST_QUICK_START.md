# Test VPN Bypass Fix - Quick Start Card

## ⚡ Super Quick Start (1 minute)

```powershell
cd D:\PiRouter
.\test_vpn_bypass.ps1
```

That's it! The script does everything.

---

## 📊 What You'll See

**Script Output:**
```
Bypass ON  (Direct WAN): 203.0.113.45
Bypass OFF (Via VPN):    198.51.100.200

✅ SUCCESS: IPs are different!
✅ Bypass ON routes directly (ISP IP)
✅ Bypass OFF routes through VPN (VPN IP)
```

**This means the fix is working!**

---

## 🔧 Manual Testing (If You Don't Trust the Script)

### Step 1: Disable WiFi
```powershell
netsh interface set interface "WiFi" disabled
```

### Step 2: Test Bypass ON (Direct)
```powershell
Invoke-WebRequest -Uri "https://api.ipify.org" -UseBasicParsing
# Shows: Your ISP's public IP
```

### Step 3: Disable Bypass (Enable VPN)
```powershell
Invoke-WebRequest -Uri "http://192.168.6.1:51508/api/devices/00:d8:61:34:29:8a/bypass" `
    -Method POST -Body "bypass=false"
Start-Sleep -Seconds 3
```

### Step 4: Test Bypass OFF (Via VPN)
```powershell
Invoke-WebRequest -Uri "https://api.ipify.org" -UseBasicParsing
# Shows: Different IP (VPN server's IP)
```

### Step 5: Re-enable WiFi
```powershell
netsh interface set interface "WiFi" enabled
```

---

## ✅ Success Criteria

- [ ] **Two different public IPs** shown (one for direct, one for VPN)
- [ ] **Both have internet access** (no timeouts)
- [ ] **Response times < 5 seconds** for both
- [ ] **No errors** in console output

---

## ❌ Troubleshooting

### "Both IPs are the same"
→ WiFi probably still active
→ Run `netsh interface set interface "WiFi" disabled` again
→ Test again

### "Timeout errors"
→ VPN might not be connected
→ Check: `curl http://192.168.6.1:51508/api/vpn/status`
→ Should show `"connected": true`

### "Can't reach api.ipify.org"
→ Try alternative: `https://checkip.amazonaws.com`
→ Or: `https://ifconfig.me`

---

## 📍 Your Setup

| Item | Value |
|------|-------|
| **Pi IP** | 192.168.6.1 |
| **Your PC on LAN** | 192.168.6.186 |
| **Your MAC** | 00:d8:61:34:29:8a |
| **VPN Profile** | wg-london-st003 |

---

## 📚 More Info

- **Full Testing Guide:** `.ai/TESTING_GUIDE_PUBLIC_IP.md`
- **Fix Summary:** `.ai/EXECUTIVE_SUMMARY_VPN_FIX.md`
- **Technical Details:** `.ai/VPN_BYPASS_FIX_FINAL.md`

---

**That's all you need to know!** Just run the script and check if the IPs are different. ✅
