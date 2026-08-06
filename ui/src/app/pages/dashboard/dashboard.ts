import { Component, OnInit, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { RouterStore, formatAge, formatBytes } from '../../core/router-store';

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardPage implements OnInit {
  protected readonly store = inject(RouterStore);
  protected readonly formatBytes = formatBytes;
  protected readonly formatAge = formatAge;

  protected readonly statusHeadline = computed(() => {
    switch (this.store.connectionState()) {
      case 'connected': return 'Protected';
      case 'degraded': return 'Tunnel stalled';
      default: return 'Not protected';
    }
  });

  protected readonly statusDetail = computed(() => {
    const status = this.store.vpnStatus();
    if (!status?.connected) return 'Traffic is going straight out to the internet.';
    if (!status.healthy) return 'The tunnel is up but has not handshaken recently.';
    return `Traffic is routed through ${status.profile ?? 'the tunnel'}.`;
  });

  ngOnInit(): void {
    this.store.refreshProfiles();
    this.store.refreshDevices();
    this.store.refreshDomains();
    this.store.refreshSystemInfo();
  }
}
