import { Injectable, computed, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, of } from 'rxjs';
import { Api } from './api';
import {
  Device, Devices, DomainBypass, Domains, SystemInfo, VpnProfile, VpnProfiles, VpnStatus,
} from './api.models';

/**
 * Shared signal store for everything more than one page needs.
 *
 * VPN status is polled once here and read by every page, instead of each component running
 * its own `interval(5000)` as the old UI did — three pages open meant three independent
 * polling loops all hitting the same endpoint.
 */
@Injectable({ providedIn: 'root' })
export class RouterStore {
  private readonly api = inject(Api);
  private readonly snackbar = inject(MatSnackBar);

  readonly vpnStatus = signal<VpnStatus | null>(null);
  readonly profiles = signal<VpnProfile[]>([]);
  readonly killSwitch = signal(false);
  readonly devices = signal<Device[]>([]);
  readonly domains = signal<DomainBypass[]>([]);
  readonly systemInfo = signal<SystemInfo | null>(null);

  readonly busy = signal(false);
  readonly lastError = signal<string | null>(null);

  readonly connected = computed(() => this.vpnStatus()?.connected ?? false);
  readonly healthy = computed(() => this.vpnStatus()?.healthy ?? false);
  readonly onlineDevices = computed(() => this.devices().filter((d) => d.online).length);
  readonly bypassedDevices = computed(() => this.devices().filter((d) => d.bypassVpn).length);

  /** Connection state as a single value, so the UI never has to derive it in three places. */
  readonly connectionState = computed<'connected' | 'degraded' | 'disconnected'>(() => {
    const status = this.vpnStatus();
    if (!status?.connected) return 'disconnected';
    return status.healthy ? 'connected' : 'degraded';
  });

  private pollHandle?: ReturnType<typeof setInterval>;

  startPolling(intervalMs = 5000): void {
    this.refreshStatus();
    this.pollHandle ??= setInterval(() => this.refreshStatus(), intervalMs);
  }

  stopPolling(): void {
    if (this.pollHandle) clearInterval(this.pollHandle);
    this.pollHandle = undefined;
  }

  refreshStatus(): void {
    this.api.vpnStatus()
      .pipe(catchError((err) => { this.lastError.set(describe(err)); return of(null); }))
      .subscribe((status) => { if (status) { this.vpnStatus.set(status); this.lastError.set(null); } });
  }

  refreshProfiles(): void {
    this.api.vpnProfiles().pipe(catchError(this.report<VpnProfiles>('Could not load VPN profiles')))
      .subscribe((result) => {
        if (!result) return;
        this.profiles.set(result.profiles);
        this.killSwitch.set(result.killSwitchEnabled);
      });
  }

  refreshDevices(): void {
    this.api.devices().pipe(catchError(this.report<Devices>('Could not load devices')))
      .subscribe((result) => result && this.devices.set(result.devices));
  }

  refreshDomains(): void {
    this.api.domains().pipe(catchError(this.report<Domains>('Could not load bypass domains')))
      .subscribe((result) => result && this.domains.set(result.domains));
  }

  refreshSystemInfo(): void {
    this.api.systemInfo().pipe(catchError(this.report<SystemInfo>('Could not load system info')))
      .subscribe((result) => result && this.systemInfo.set(result));
  }

  applyDevices(result: Devices | null): void {
    if (result) this.devices.set(result.devices);
  }

  notify(message: string, kind: 'ok' | 'error' = 'ok'): void {
    this.snackbar.open(message, 'Dismiss', {
      duration: kind === 'ok' ? 4000 : 8000,
      panelClass: kind === 'ok' ? 'snack-ok' : 'snack-error',
      horizontalPosition: 'end',
      verticalPosition: 'bottom',
    });
  }

  /** Wraps a call so failures surface in the snackbar instead of the console. */
  report<T>(context: string) {
    return (err: unknown) => {
      const message = `${context}: ${describe(err)}`;
      this.lastError.set(message);
      this.notify(message, 'error');
      return of(null as T | null);
    };
  }
}

/** Turns an HttpErrorResponse into something worth showing a person. */
export function describe(err: unknown): string {
  if (typeof err === 'object' && err !== null) {
    const e = err as { error?: { error?: string }; status?: number; message?: string };
    if (e.error?.error) return e.error.error;
    if (e.status === 0) return 'the router is not reachable';
    if (e.status) return `HTTP ${e.status}`;
    if (e.message) return e.message;
  }
  return String(err);
}

/** 1234567 -> "1.2 MB". Used for tunnel counters. */
export function formatBytes(value: number): string {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`;
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

/** Seconds -> "12s" / "3m 4s". Used for handshake age. */
export function formatAge(seconds: number | null | undefined): string {
  if (seconds == null) return 'never';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${Math.round(seconds % 60)}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
