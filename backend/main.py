"""
PiRouter VPN Manager - Main API Server

A FastAPI-based backend for managing WireGuard VPN connections,
device bypass routing, and kill switch functionality.
"""
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

# Import route handlers
import vpn_routes
import device_routes
import domain_routes
import system_routes

# Create FastAPI app
app = FastAPI(
    title="PiRouter VPN Manager",
    version="1.0.0",
    description="Manage WireGuard VPN connections with per-device routing"
)

# CORS middleware for frontend
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Include routers
app.include_router(vpn_routes.router, prefix="/api/vpn", tags=["VPN"])
app.include_router(device_routes.router, prefix="/api/devices", tags=["Devices"])
app.include_router(domain_routes.router, prefix="/api/domains", tags=["Domains"])
app.include_router(system_routes.router, prefix="/api/system", tags=["System"])


@app.get("/")
async def root():
    """Root endpoint - API information"""
    return {
        "message": "PiRouter VPN Manager API",
        "version": "1.0.0",
        "docs": "/docs",
        "redoc": "/redoc"
    }


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=51507)
