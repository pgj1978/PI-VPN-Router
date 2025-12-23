import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ApiService, VpnStatus, IpConfig, Device, DomainBypass } from '../services/api.service';
import { interval } from 'rxjs';
import { startWith, switchMap } from 'rxjs/operators';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css'
})
export class DashboardComponent implements OnInit {
  title = 'System Overview';
  vpnStatus: VpnStatus | null = null;
  ipConfig: IpConfig | null = null;
  deviceCount: number = 0;
  bypassCount: number = 0;
  
  constructor(private apiService: ApiService) {}

  ngOnInit() {
    this.loadStats();
    this.loadIpConfig();

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

  loadStats() {
    this.apiService.getDevices().subscribe(res => this.deviceCount = res.devices.length);
    this.apiService.getBypassDomains().subscribe(res => this.bypassCount = res.domains.length);
  }

  loadIpConfig() {
    this.apiService.getEth1IpConfig().subscribe(res => this.ipConfig = res);
  }

  getStatusColor(): string {
    return this.vpnStatus?.connected ? '#28a745' : '#dc3545';
  }

  getStatusText(): string {
    if (!this.vpnStatus) return 'Loading...';
    return this.vpnStatus.connected 
      ? `VPN Connected (${this.vpnStatus.interface_name})` 
      : 'VPN Disconnected';
  }
}