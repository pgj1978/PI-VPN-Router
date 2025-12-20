#!/bin/bash
# Start Angular frontend for PiRouter on all network interfaces

cd /home/pgj99/code/PiRouter/frontend

# Kill any existing ng serve processes
pkill -f "ng serve" 2>/dev/null

# Start Angular dev server on all interfaces
npx ng serve --host 0.0.0.0 --disable-host-check
