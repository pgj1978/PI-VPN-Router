# Copy updated Sidebar files
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/sidebar/sidebar.component.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/sidebar/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/sidebar/sidebar.component.html pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/sidebar/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/sidebar/sidebar.component.css pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/sidebar/

# Copy updated App files
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/app.component.ts pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/app.component.html pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/app.component.css pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/

# Copy updated VPN CSS
scp -i C:\Users\pgj99\.ssh\id_ed25519 frontend/src/app/vpn/vpn.component.css pgj99@192.168.5.109:/home/pgj99/code/PiRouter/frontend/src/app/vpn/

# Rebuild
ssh -i C:\Users\pgj99\.ssh\id_ed25519 pgj99@192.168.5.109 "cd /home/pgj99/code/PiRouter && docker compose build frontend && docker compose up -d"
