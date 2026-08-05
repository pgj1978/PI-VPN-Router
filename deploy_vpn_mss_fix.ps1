# VPN MSS Clamping Fix Deployment Script
# This script builds and deploys the updated VpnManager.cs to the Pi

param(
    [string]$PiIp = "192.168.10.1",
    [string]$SshUser = "pgj99",
    [string]$SshKey = "C:\Users\pgj99\.ssh\id_ed25519"
)

Write-Host "=== VPN MSS Clamping Fix Deployment ===" -ForegroundColor Cyan
Write-Host "Target Pi: $PiIp"
Write-Host "User: $SshUser"
Write-Host ""

# Step 1: Build locally
Write-Host "Step 1: Building backend locally..." -ForegroundColor Yellow
cd D:\PiRouter\PiRouterBackend
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Step 2: Copy to Pi
Write-Host "Step 2: Copying files to Pi..." -ForegroundColor Yellow
scp -i $SshKey -r D:\PiRouter\PiRouterBackend pgj99@${PiIp}:/home/pgj99/code/PiRouter/
if ($LASTEXITCODE -ne 0) {
    Write-Host "SCP failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Files copied successfully!" -ForegroundColor Green
Write-Host ""

# Step 3: Build Docker image on Pi
Write-Host "Step 3: Building Docker image on Pi..." -ForegroundColor Yellow
ssh -i $SshKey pgj99@${PiIp} "cd /home/pgj99/code/PiRouter && docker compose build --no-cache backend-csharp"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Docker build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Docker image built successfully!" -ForegroundColor Green
Write-Host ""

# Step 4: Restart container
Write-Host "Step 4: Restarting backend container..." -ForegroundColor Yellow
ssh -i $SshKey pgj99@${PiIp} "docker compose up -d backend-csharp && sleep 5 && docker logs --tail 30 pirouter-backend-csharp"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Container restart failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Container restarted successfully!" -ForegroundColor Green
Write-Host ""

# Step 5: Verify API
Write-Host "Step 5: Verifying API connectivity..." -ForegroundColor Yellow
Start-Sleep -Seconds 3
$response = Invoke-WebRequest -Uri "http://${PiIp}:51508/api/vpn/status" -UseBasicParsing -ErrorAction SilentlyContinue
if ($response) {
    Write-Host "API responding correctly!" -ForegroundColor Green
    Write-Host "Response: $($response.Content)"
} else {
    Write-Host "WARNING: Could not verify API response" -ForegroundColor Yellow
}
Write-Host ""

Write-Host "=== Deployment Complete ===" -ForegroundColor Green
Write-Host "Next Steps:"
Write-Host "1. Test with bypass DISABLED (traffic through VPN)"
Write-Host "2. Test with bypass ENABLED (traffic bypasses VPN)"
Write-Host "3. Verify TCP/HTTPS connectivity works in both modes"
