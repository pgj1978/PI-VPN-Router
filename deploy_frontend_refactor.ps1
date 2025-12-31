# Update frontend code
# Copy new VPN component
scp -r -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/vpn pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/

# Copy updated Dashboard component
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/dashboard/dashboard.component.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/dashboard/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/dashboard/dashboard.component.html pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/dashboard/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/dashboard/dashboard.component.css pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/dashboard/

# Copy updated app routes and sidebar
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/app.routes.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/services/api.service.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/services/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/sidebar/sidebar.component.html pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/sidebar/

# Rebuild frontend on Pi
ssh -i C:\Users\pgj99\.ssh\id_ed25519 pgj99@192.168.5.109 "cd /home/pgj99/code/PiRouter && docker compose build frontend && docker compose up -d"
