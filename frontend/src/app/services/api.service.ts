import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface VpnConfig {
  name: string;
  filename: string;
  active: boolean;
  is_current: boolean;
}

export interface VpnStatus {
  connected: boolean;
  interface: string | null;
  details: string | null;
}

export interface Device {
  mac: string;
  ip: string;
  hostname: string | null;
  bypass_vpn: boolean;
  static_ip?: string | null;
}

export interface DomainBypass {
  domain: string;
  enabled: boolean;
}

export interface DhcpStatus {
  enabled: boolean;
  dhcpRange?: string; // e.g., "192.168.10.100,192.168.10.200"
  leaseTime?: string; // e.g., "12h"
  error?: string;
}

export interface IpConfig {
  ipAddress?: string;
  subnetMask?: string;
  error?: string;
}

@Injectable({
  providedIn: 'root'
})
export class ApiService {
  private apiUrl = `http://${window.location.hostname}:51508/api`;

  constructor(private http: HttpClient) { }

  getVpnConfigs(): Observable<{ configs: VpnConfig[], kill_switch_enabled: boolean }> {
    return this.http.get<{ configs: VpnConfig[], kill_switch_enabled: boolean }>(`${this.apiUrl}/vpn/configs`);
  }

  getVpnStatus(): Observable<VpnStatus> {
    return this.http.get<VpnStatus>(`${this.apiUrl}/vpn/status`);
  }

  connectVpn(configName: string): Observable<any> {
    return this.http.post(`${this.apiUrl}/vpn/connect/${configName}`, {});
  }

  disconnectVpn(): Observable<any> {
    return this.http.post(`${this.apiUrl}/vpn/disconnect`, {});
  }

  toggleKillSwitch(enabled: boolean): Observable<any> {
    return this.http.post(`${this.apiUrl}/vpn/kill-switch?enabled=${enabled}`, {});
  }

  getDevices(): Observable<{ devices: Device[] }> {
    return this.http.get<{ devices: Device[] }>(`${this.apiUrl}/devices`);
  }

  setDeviceBypass(mac: string, bypass: boolean): Observable<any> {
    return this.http.post(`${this.apiUrl}/devices/${mac}/bypass?bypass=${bypass}`, {});
  }

  setDeviceStaticIp(mac: string, ip: string | null): Observable<any> {
    let url = `${this.apiUrl}/devices/${mac}/static-ip`;
    if (ip) {
      url += `?ip=${ip}`;
    }
    return this.http.post(url, {});
  }

  getBypassDomains(): Observable<{ domains: DomainBypass[] }> {
    return this.http.get<{ domains: DomainBypass[] }>(`${this.apiUrl}/domains/bypass`);
  }

  addBypassDomain(domain: string): Observable<any> {
    return this.http.post(`${this.apiUrl}/domains/bypass?domain=${encodeURIComponent(domain)}`, {});
  }

  removeBypassDomain(domain: string): Observable<any> {
    return this.http.delete(`${this.apiUrl}/domains/bypass?domain=${encodeURIComponent(domain)}`);
  }

  getSystemInfo(): Observable<any> {
    return this.http.get(`${this.apiUrl}/system/info`);
  }

  getDhcpStatus(): Observable<DhcpStatus> {
    return this.http.get<DhcpStatus>(`${this.apiUrl}/system/dhcp`);
  }

  setDhcpStatus(enable: boolean, startIp?: string, endIp?: string, leaseTime?: string): Observable<any> {
    let url = `${this.apiUrl}/system/dhcp?enable=${enable}`;
    if (startIp && endIp) {
      url += `&startIp=${startIp}&endIp=${endIp}`;
    }
    if (leaseTime) {
      url += `&leaseTime=${leaseTime}`;
    }
    return this.http.post(url, {});
  }

  getEth1IpConfig(): Observable<IpConfig> {
    return this.http.get<IpConfig>(`${this.apiUrl}/system/eth1-ip`);
  }

  setEth1IpConfig(ipAddress: string, subnetMask: string): Observable<any> {
    return this.http.post(`${this.apiUrl}/system/eth1-ip?ipAddress=${ipAddress}&subnetMask=${subnetMask}`, {});
  }

  rebootSystem(): Observable<any> {
    return this.http.post(`${this.apiUrl}/system/reboot`, {});
  }
}
