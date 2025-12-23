import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, VpnConfig, VpnStatus, Device, DomainBypass } from '../services/api.service';
import { interval } from 'rxjs';
import { startWith, switchMap } from 'rxjs/operators';

type Tab = 'connections' | 'devices' | 'bypass';

@Component({
  selector: 'app-vpn',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './vpn.component.html',
  styleUrl: './vpn.component.css'
})
export class VpnComponent implements OnInit {
  activeTab: Tab = 'connections';
  
  vpnConfigs: VpnConfig[] = [];
  vpnStatus: VpnStatus | null = null;
  devices: Device[] = [];
  bypassDomains: DomainBypass[] = [];
  killSwitchEnabled = false;
  newDomain = '';
  loading = false;
  message: string | null = null;

  // Connection Logs
  showLogs = false;
  connectionLogs = '';
  connectionTitle = '';

  constructor(private apiService: ApiService) {}

  ngOnInit() {
    this.loadVpnConfigs();
    this.loadDevices();
    this.loadBypassDomains();
    
    // Poll VPN status every 5 seconds
    interval(5000)
      .pipe(
        startWith(0),
        switchMap(() => this.apiService.getVpnStatus())
      )
      .subscribe({
        next: (status) => this.vpnStatus = status,
        error: (err) => console.error('Error fetching VPN status:', err)
      });
  }

  setActiveTab(tab: Tab) {
    this.activeTab = tab;
  }

  loadVpnConfigs() {
    this.apiService.getVpnConfigs().subscribe({
      next: (response) => {
        this.vpnConfigs = response.configs;
        this.killSwitchEnabled = response.kill_switch_enabled;
      },
      error: (err) => this.showMessage('Error loading VPN configs: ' + err.message, 'error')
    });
  }

  loadDevices() {
    this.apiService.getDevices().subscribe({
      next: (response) => this.devices = response.devices,
      error: (err) => this.showMessage('Error loading devices: ' + err.message, 'error')
    });
  }

  loadBypassDomains() {
    this.apiService.getBypassDomains().subscribe({
      next: (response) => this.bypassDomains = response.domains,
      error: (err) => console.error('Error loading bypass domains:', err)
    });
  }

  connectVpn(configName: string) {
    this.loading = true;
    this.connectionTitle = `Connecting to ${configName}...`;
    this.connectionLogs = 'Initiating connection request...';
    this.showLogs = true;

    this.apiService.connectVpn(configName).subscribe({
      next: (res: any) => {
        this.loading = false;
        if (res.logs) {
            this.connectionLogs = res.logs;
        } else {
            this.connectionLogs = "Connection successful (No output captured).";
        }
        
        if (res.success) {
            this.connectionTitle = `Connected to ${configName}`;
            this.showMessage(`Connected to ${configName}`, 'success');
            this.loadVpnConfigs();
        } else {
            this.connectionTitle = `Connection Failed`;
        }
      },
      error: (err) => {
        this.loading = false;
        this.connectionTitle = `Error Connecting to ${configName}`;
        this.connectionLogs = err.error?.logs || err.message;
        this.showMessage('Error connecting: ' + err.message, 'error');
      }
    });
  }

  closeLogs() {
    this.showLogs = false;
    this.connectionLogs = '';
  }

  disconnectVpn() {
    this.loading = true;
    this.apiService.disconnectVpn().subscribe({
      next: () => {
        this.showMessage('VPN disconnected', 'success');
        this.loading = false;
        this.loadVpnConfigs();
      },
      error: (err) => {
        this.showMessage('Error disconnecting: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  toggleKillSwitch(event: any) {
    const enabled = event.target.checked;
    this.loading = true;
    this.apiService.toggleKillSwitch(enabled).subscribe({
      next: () => {
        this.killSwitchEnabled = enabled;
        this.showMessage(`Kill switch ${enabled ? 'enabled' : 'disabled'}`, 'success');
        this.loading = false;
      },
      error: (err) => {
        this.showMessage('Error toggling kill switch: ' + err.message, 'error');
        this.loading = false;
        event.target.checked = !enabled;
      }
    });
  }

  toggleDeviceBypass(device: Device) {
    const newBypass = !device.bypass_vpn;
    this.apiService.setDeviceBypass(device.mac, newBypass).subscribe({
      next: () => {
        device.bypass_vpn = newBypass;
        this.showMessage(
          `Device ${device.hostname || device.ip} ${newBypass ? 'will bypass' : 'will use'} VPN`,
          'success'
        );
      },
      error: (err) => this.showMessage('Error updating device: ' + err.message, 'error')
    });
  }

  addDomain() {
    if (!this.newDomain) return;
    
    this.loading = true;
    this.apiService.addBypassDomain(this.newDomain).subscribe({
      next: () => {
        this.showMessage(`Domain ${this.newDomain} added to bypass list`, 'success');
        this.newDomain = '';
        this.loadBypassDomains();
        this.loading = false;
      },
      error: (err) => {
        this.showMessage('Error adding domain: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  removeDomain(domain: string) {
    this.loading = true;
    this.apiService.removeBypassDomain(domain).subscribe({
      next: () => {
        this.showMessage(`Domain ${domain} removed from bypass list`, 'success');
        this.loadBypassDomains();
        this.loading = false;
      },
      error: (err) => {
        this.showMessage('Error removing domain: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  showMessage(msg: string, type: string) {
    this.message = msg;
    setTimeout(() => this.message = null, 5000);
  }

  getStatusColor(): string {
    return this.vpnStatus?.connected ? '#28a745' : '#dc3545';
  }

  getStatusText(): string {
    if (!this.vpnStatus) return 'Loading...';
    return this.vpnStatus.connected 
      ? `Connected (${this.vpnStatus.interface_name})` 
      : 'Disconnected';
  }
}
