import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

import { RouterStore } from './core/router-store';

interface NavItem {
  path: string;
  label: string;
  icon: string;
}

@Component({
  selector: 'app-root',
  imports: [
    RouterOutlet, RouterLink, RouterLinkActive,
    MatSidenavModule, MatToolbarModule, MatListModule,
    MatIconModule, MatButtonModule, MatTooltipModule,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  private readonly breakpoints = inject(BreakpointObserver);
  protected readonly store = inject(RouterStore);

  protected readonly nav: NavItem[] = [
    { path: '/dashboard', label: 'Dashboard', icon: 'dashboard' },
    { path: '/vpn', label: 'VPN', icon: 'vpn_lock' },
    { path: '/devices', label: 'Devices', icon: 'devices' },
    { path: '/domains', label: 'Domain bypass', icon: 'dns' },
    { path: '/logs', label: 'Logs', icon: 'receipt_long' },
    { path: '/diagnostics', label: 'Diagnostics', icon: 'health_and_safety' },
    { path: '/system', label: 'System', icon: 'settings' },
  ];

  /** Below the medium breakpoint the drawer becomes an overlay rather than a fixed rail. */
  protected readonly handset = toSignal(
    this.breakpoints.observe([Breakpoints.Handset, Breakpoints.Small]).pipe(map((r) => r.matches)),
    { initialValue: false },
  );

  protected readonly drawerOpen = signal(true);
  protected readonly drawerMode = computed<'side' | 'over'>(() => (this.handset() ? 'over' : 'side'));

  protected readonly statusLabel = computed(() => {
    switch (this.store.connectionState()) {
      case 'connected': return 'VPN connected';
      case 'degraded': return 'VPN degraded - no recent handshake';
      default: return 'VPN disconnected';
    }
  });

  ngOnInit(): void {
    this.store.startPolling();
    this.drawerOpen.set(!this.handset());
  }

  ngOnDestroy(): void {
    this.store.stopPolling();
  }

  protected toggleDrawer(): void {
    this.drawerOpen.update((open) => !open);
  }

  /** On a phone the drawer is an overlay, so following a link should dismiss it. */
  protected onNavigate(): void {
    if (this.handset()) this.drawerOpen.set(false);
  }
}
