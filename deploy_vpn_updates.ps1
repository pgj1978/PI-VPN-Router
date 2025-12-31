# Update backend
scp -i C:\Users\pgj99\.ssh\id_ed25519 PiRouterBackend/Services/VpnManager.cs pgj99@192.168.5.109:/home/pgj99/code/PiRouter/PiRouterBackend/Services/
scp -i C:\Users\pgj99\.ssh\id_ed25519 PiRouterBackend/Program.cs pgj99@192.168.5.109:/home/pgj99/code/PiRouter/PiRouterBackend/

# Update frontend code
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/dashboard/dashboard.component.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/dashboard/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/dashboard/dashboard.component.html pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/dashboard/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/dashboard/dashboard.component.css pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/dashboard/

# Rebuild on Pi
ssh -i C:\Users\pgj99\.ssh\id_ed25519 pgj99@192.168.5.109 "cd /home/pgj99/code/PiRouter && docker compose build backend-csharp frontend && docker compose up -d"
