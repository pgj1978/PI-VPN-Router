import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, Device, DhcpStatus, IpConfig } from '../services/api.service';

@Component({
  selector: 'app-routing',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './routing.component.html',
  styleUrl: './routing.component.css'
})
export class RoutingComponent implements OnInit {
  devices: Device[] = [];
  loading = false;
  message: string | null = null;

  dhcpEnabled: boolean = false;
  dhcpStartIp: string = '';
  dhcpEndIp: string = '';
  dhcpLeaseTime: string = '12h';

  eth1IpAddress: string = '';
  eth1SubnetMask: string = '';

  constructor(private apiService: ApiService) {}

  ngOnInit() {
    this.loadDevices();
    this.loadDhcpStatus();
    this.loadEth1IpConfig();
  }

  loadDevices() {
    this.loading = true;
    this.apiService.getDevices().subscribe({
      next: (response) => {
        this.devices = response.devices;
        this.loading = false;
      },
      error: (err) => {
        this.showMessage('Error loading devices: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  loadDhcpStatus() {
    this.apiService.getDhcpStatus().subscribe({
      next: (status: DhcpStatus) => {
        this.dhcpEnabled = status.enabled;
        if (status.dhcpRange) {
          const [start, end] = status.dhcpRange.split(',');
          this.dhcpStartIp = start;
          this.dhcpEndIp = end;
        }
        this.dhcpLeaseTime = status.leaseTime || '12h';
      },
      error: (err) => this.showMessage('Error loading DHCP status: ' + err.message, 'error')
    });
  }

  saveDhcpSettings() {
    this.loading = true;
    let startIp = this.dhcpEnabled ? this.dhcpStartIp : undefined;
    let endIp = this.dhcpEnabled ? this.dhcpEndIp : undefined;
    let leaseTime = this.dhcpEnabled ? this.dhcpLeaseTime : undefined;

    this.apiService.setDhcpStatus(this.dhcpEnabled, startIp, endIp, leaseTime).subscribe({
      next: () => {
        this.showMessage('DHCP settings saved successfully', 'success');
        this.loading = false;
        this.loadDhcpStatus(); // Reload to confirm
      },
      error: (err) => {
        this.showMessage('Error saving DHCP settings: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  loadEth1IpConfig() {
    this.apiService.getEth1IpConfig().subscribe({
      next: (config: IpConfig) => {
        this.eth1IpAddress = config.ipAddress || '';
        this.eth1SubnetMask = config.subnetMask || '';
      },
      error: (err) => this.showMessage('Error loading eth1 IP config: ' + err.message, 'error')
    });
  }

  saveEth1IpConfig() {
    this.loading = true;
    this.apiService.setEth1IpConfig(this.eth1IpAddress, this.eth1SubnetMask).subscribe({
      next: () => {
        this.showMessage('eth1 IP settings saved successfully. Apply may require network restart.', 'success');
        this.loading = false;
        this.loadEth1IpConfig(); // Reload to confirm
      },
      error: (err) => {
        this.showMessage('Error saving eth1 IP settings: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  saveStaticIp(device: Device) {
    this.loading = true;
    this.apiService.setDeviceStaticIp(device.mac, device.static_ip || null).subscribe({
      next: () => {
        this.showMessage(`Static IP for ${device.hostname || device.mac} saved.`, 'success');
        this.loading = false;
      },
      error: (err) => {
        this.showMessage('Error saving static IP: ' + err.message, 'error');
        this.loading = false;
      }
    });
  }

  showMessage(msg: string, type: string) {
    this.message = msg;
    setTimeout(() => this.message = null, 5000);
  }

  // saveSettings() { // This was a placeholder, actual saves are now specific to DHCP/IP
  //   console.log('Saving routing settings (Not implemented on backend yet)');
  //   alert('Backend support for static IPs is coming soon.');
  // }
}