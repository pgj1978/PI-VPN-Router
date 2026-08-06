// Mirrors PiRouter.Api.Contracts. Kept hand-written but strictly typed so a field rename on
// the server is a compile error here rather than an `undefined` at runtime — the previous UI
// was untyped enough to call an endpoint that did not exist without anyone noticing.

export interface VpnStatus {
  connected: boolean;
  profile: string | null;
  interfaceName: string;
  endpoint: string | null;
  handshakeAgeSeconds: number | null;
  bytesReceived: number;
  bytesSent: number;
  healthy: boolean;
}

export interface VpnProfile {
  name: string;
  endpoint: string | null;
  dns: string | null;
  mtu: number | null;
  active: boolean;
}

export interface VpnProfiles {
  profiles: VpnProfile[];
  killSwitchEnabled: boolean;
  activeProfile: string | null;
}

export interface VpnOperation {
  success: boolean;
  error: string | null;
  log: string;
}

export interface Device {
  mac: string;
  ip: string | null;
  hostname: string | null;
  label: string | null;
  bypassVpn: boolean;
  staticIp: string | null;
  online: boolean;
  leaseExpires: string | null;
}

export interface Devices {
  devices: Device[];
}

export interface DomainBypass {
  domain: string;
  enabled: boolean;
  resolvedIps: string[];
  lastResolvedAt: string | null;
}

export interface Domains {
  domains: DomainBypass[];
}

export interface Dhcp {
  enabled: boolean;
  rangeStart: string;
  rangeEnd: string;
  leaseTime: string;
}

export interface SystemInfo {
  lanInterface: string;
  wanInterface: string;
  vpnInterface: string;
  lanAddress: string | null;
  wanAddress: string | null;
  wanGateway: string | null;
  ipForwarding: boolean;
  dnsmasqRunning: boolean;
  version: string;
  uptime: string;
}

export interface RulePreview {
  fingerprint: string;
  rules: string[];
  missing: string[];
  unexpected: string[];
  missingChains: string[];
  inSync: boolean;
  lastReconciledAt: string | null;
}

export type CheckStatus = 'pass' | 'warn' | 'fail';

export interface DiagnosticCheck {
  id: string;
  name: string;
  status: CheckStatus;
  detail: string;
  remediation: string | null;
}

export interface Diagnostics {
  ranAt: string;
  overall: CheckStatus;
  checks: DiagnosticCheck[];
}

export type LogLevel = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';

export interface LogEntry {
  sequence: number;
  timestamp: string;
  level: LogLevel;
  category: string;
  message: string;
  exception: string | null;
}

export interface Logs {
  entries: LogEntry[];
}
