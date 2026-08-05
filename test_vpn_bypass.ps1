param(
    [string]$PiIp = "192.168.6.1",
    [string]$Mac = "00:d8:61:34:29:8a",
    [bool]$DisableWifi = $true
)

Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║          VPN Bypass Testing Script                            ║" -ForegroundColor Cyan
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

# Step 1: Disable WiFi if requested
if ($DisableWifi) {
    Write-Host ""
    Write-Host "Step 1: Disabling WiFi..." -ForegroundColor Yellow
    netsh interface set interface "WiFi" disabled
    Start-Sleep -Seconds 2
    Write-Host "✓ WiFi disabled" -ForegroundColor Green
}

# Step 2: Verify LAN only
Write-Host ""
Write-Host "Step 2: Verifying network connectivity..." -ForegroundColor Yellow
$adapters = Get-NetAdapter | Where-Object Status -eq "Up"
foreach ($adapter in $adapters) {
    Write-Host "  Active: $($adapter.Name) - $($adapter.InterfaceDescription)" -ForegroundColor Green
}

# Step 3: Test Bypass ON
Write-Host ""
Write-Host "Step 3: Testing Bypass ON (Direct WAN)..." -ForegroundColor Yellow
try {
    $ip = Invoke-WebRequest -Uri "https://api.ipify.org" -UseBasicParsing -TimeoutSec 10
    $bypassOnIp = $ip.Content
    Write-Host "  Public IP: $bypassOnIp" -ForegroundColor Green
} catch {
    Write-Host "  ✗ Test failed: $_" -ForegroundColor Red
    exit 1
}

# Step 4: Disable Bypass
Write-Host ""
Write-Host "Step 4: Disabling Bypass (routing through VPN)..." -ForegroundColor Yellow
try {
    Invoke-WebRequest -Uri "http://${PiIp}:51508/api/devices/${Mac}/bypass" `
        -Method POST -UseBasicParsing -Body "bypass=false" | Out-Null
    Write-Host "✓ Bypass disabled" -ForegroundColor Green
    Start-Sleep -Seconds 3
} catch {
    Write-Host "✗ Failed to disable bypass: $_" -ForegroundColor Red
    exit 1
}

# Step 5: Test Bypass OFF
Write-Host ""
Write-Host "Step 5: Testing Bypass OFF (Via VPN)..." -ForegroundColor Yellow
try {
    $ip = Invoke-WebRequest -Uri "https://api.ipify.org" -UseBasicParsing -TimeoutSec 10
    $bypassOffIp = $ip.Content
    Write-Host "  Public IP: $bypassOffIp" -ForegroundColor Green
} catch {
    Write-Host "  ✗ Test failed: $_" -ForegroundColor Red
    exit 1
}

# Step 6: Re-enable Bypass
Write-Host ""
Write-Host "Step 6: Re-enabling Bypass..." -ForegroundColor Yellow
try {
    Invoke-WebRequest -Uri "http://${PiIp}:51508/api/devices/${Mac}/bypass" `
        -Method POST -UseBasicParsing -Body "bypass=true" | Out-Null
    Write-Host "✓ Bypass re-enabled" -ForegroundColor Green
} catch {
    Write-Host "✗ Failed to re-enable bypass: $_" -ForegroundColor Red
}

# Step 7: Re-enable WiFi if requested
if ($DisableWifi) {
    Write-Host ""
    Write-Host "Step 7: Re-enabling WiFi..." -ForegroundColor Yellow
    netsh interface set interface "WiFi" enabled
    Start-Sleep -Seconds 2
    Write-Host "✓ WiFi re-enabled" -ForegroundColor Green
}

# Results Summary
Write-Host ""
Write-Host "╔════════════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║                    TEST RESULTS                               ║" -ForegroundColor Green
Write-Host "╚════════════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "Bypass ON  (Direct WAN): $bypassOnIp" -ForegroundColor Cyan
Write-Host "Bypass OFF (Via VPN):    $bypassOffIp" -ForegroundColor Cyan
Write-Host ""

if ($bypassOnIp -eq $bypassOffIp) {
    Write-Host "⚠️  WARNING: Both IPs are the same!" -ForegroundColor Yellow
    Write-Host "This might mean:" -ForegroundColor Yellow
    Write-Host "  • VPN server's public IP matches ISP's IP (unlikely)" -ForegroundColor Yellow
    Write-Host "  • Traffic not actually routing through VPN" -ForegroundColor Yellow
    Write-Host "  • WiFi is still being used despite WiFi disable" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Try: Verify with 'tracert 8.8.8.8' to see the actual route" -ForegroundColor Yellow
} else {
    Write-Host "✅ SUCCESS: IPs are different!" -ForegroundColor Green
    Write-Host "✅ Bypass ON routes directly (ISP IP)" -ForegroundColor Green
    Write-Host "✅ Bypass OFF routes through VPN (VPN IP)" -ForegroundColor Green
}
