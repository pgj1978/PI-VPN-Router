# Update backend
scp -i C:\Users\pgj99\.ssh\id_ed25519 PiRouterBackend/Services/SystemManager.cs pgj99@192.168.5.109:/home/pgj99/code/PiRouter/PiRouterBackend/Services/
scp -i C:\Users\pgj99\.ssh\id_ed25519 PiRouterBackend/Controllers/SystemController.cs pgj99@192.168.5.109:/home/pgj99/code/PiRouter/PiRouterBackend/Controllers/

# Update frontend code (need to copy new folder and updated files)
# Recursively copy the new system folder
scp -r -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/system pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/
# Update existing frontend files
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/app.routes.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/services/api.service.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/services/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/sidebar/sidebar.component.html pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/sidebar/

# Rebuild on Pi
ssh -i C:\Users\pgj99\.ssh\id_ed25519 pgj99@192.168.5.109 "cd /home/pgj99/code/PiRouter && docker compose build backend-csharp frontend && docker compose up -d"
