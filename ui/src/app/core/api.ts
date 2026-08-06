import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Devices, Diagnostics, Dhcp, Domains, Logs, RulePreview,
  SystemInfo, VpnOperation, VpnProfile, VpnProfiles, VpnStatus,
} from './api.models';

/**
 * Single typed gateway to the backend.
 *
 * The base URL is relative: nginx proxies /api to the backend on the same origin, so the UI
 * needs no knowledge of the API's port. The previous build hardcoded
 * `http://${hostname}:51508`, which forced the API to be exposed publicly and CORS wide open.
 */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  // ---- VPN
  vpnStatus(): Observable<VpnStatus> {
    return this.http.get<VpnStatus>(`${this.base}/vpn/status`);
  }
  vpnProfiles(): Observable<VpnProfiles> {
    return this.http.get<VpnProfiles>(`${this.base}/vpn/profiles`);
  }
  connect(name: string): Observable<VpnOperation> {
    return this.http.post<VpnOperation>(`${this.base}/vpn/connect/${encodeURIComponent(name)}`, {});
  }
  disconnect(): Observable<VpnOperation> {
    return this.http.post<VpnOperation>(`${this.base}/vpn/disconnect`, {});
  }
  setKillSwitch(enabled: boolean): Observable<VpnProfiles> {
    return this.http.post<VpnProfiles>(`${this.base}/vpn/kill-switch`, { enabled });
  }
  addProfile(name: string, config: string): Observable<VpnProfile> {
    return this.http.post<VpnProfile>(`${this.base}/vpn/profiles`, { name, config });
  }
  deleteProfile(name: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/vpn/profiles/${encodeURIComponent(name)}`);
  }

  // ---- devices
  devices(): Observable<Devices> {
    return this.http.get<Devices>(`${this.base}/devices`);
  }
  setBypass(mac: string, bypass: boolean): Observable<Devices> {
    return this.http.put<Devices>(`${this.base}/devices/${encodeURIComponent(mac)}/bypass`, { bypass });
  }
  setStaticIp(mac: string, ip: string | null): Observable<Devices> {
    return this.http.put<Devices>(`${this.base}/devices/${encodeURIComponent(mac)}/static-ip`, { ip });
  }
  setLabel(mac: string, label: string | null): Observable<Devices> {
    return this.http.put<Devices>(`${this.base}/devices/${encodeURIComponent(mac)}/label`, { label });
  }
  forgetDevice(mac: string): Observable<Devices> {
    return this.http.delete<Devices>(`${this.base}/devices/${encodeURIComponent(mac)}`);
  }

  // ---- domains
  domains(): Observable<Domains> {
    return this.http.get<Domains>(`${this.base}/domains`);
  }
  addDomain(domain: string): Observable<Domains> {
    return this.http.post<Domains>(`${this.base}/domains`, { domain });
  }
  removeDomain(domain: string): Observable<Domains> {
    return this.http.delete<Domains>(`${this.base}/domains/${encodeURIComponent(domain)}`);
  }
  refreshDomains(): Observable<Domains> {
    return this.http.post<Domains>(`${this.base}/domains/refresh`, {});
  }

  // ---- system
  systemInfo(): Observable<SystemInfo> {
    return this.http.get<SystemInfo>(`${this.base}/system/info`);
  }
  dhcp(): Observable<Dhcp> {
    return this.http.get<Dhcp>(`${this.base}/system/dhcp`);
  }
  setDhcp(value: Partial<Dhcp> & { enabled: boolean }): Observable<Dhcp> {
    return this.http.put<Dhcp>(`${this.base}/system/dhcp`, value);
  }
  rules(): Observable<RulePreview> {
    return this.http.get<RulePreview>(`${this.base}/system/rules`);
  }
  reconcile(): Observable<RulePreview> {
    return this.http.post<RulePreview>(`${this.base}/system/reconcile`, {});
  }
  reboot(): Observable<unknown> {
    return this.http.post(`${this.base}/system/reboot`, {});
  }

  // ---- diagnostics & logs
  diagnostics(): Observable<Diagnostics> {
    return this.http.get<Diagnostics>(`${this.base}/diagnostics`);
  }
  logs(params: { level?: string; search?: string; limit?: number } = {}): Observable<Logs> {
    const query = new URLSearchParams();
    if (params.level) query.set('level', params.level);
    if (params.search) query.set('search', params.search);
    query.set('limit', String(params.limit ?? 500));
    return this.http.get<Logs>(`${this.base}/logs?${query}`);
  }

  /** URL for the SSE log feed. Consumed via EventSource, which HttpClient cannot do. */
  logStreamUrl(params: { level?: string; search?: string } = {}): string {
    const query = new URLSearchParams();
    if (params.level) query.set('level', params.level);
    if (params.search) query.set('search', params.search);
    return `${this.base}/logs/stream?${query}`;
  }
}
