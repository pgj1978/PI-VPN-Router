import { Routes } from '@angular/router';

// Every page is lazily loaded. On a Pi served over the LAN the initial bundle matters, and
// it keeps each feature's Material imports out of the shell.
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  {
    path: 'dashboard',
    title: 'Dashboard - PiRouter',
    loadComponent: () => import('./pages/dashboard/dashboard').then((m) => m.DashboardPage),
  },
  {
    path: 'vpn',
    title: 'VPN - PiRouter',
    loadComponent: () => import('./pages/vpn/vpn').then((m) => m.VpnPage),
  },
  {
    path: 'devices',
    title: 'Devices - PiRouter',
    loadComponent: () => import('./pages/devices/devices').then((m) => m.DevicesPage),
  },
  {
    path: 'domains',
    title: 'Domain bypass - PiRouter',
    loadComponent: () => import('./pages/domains/domains').then((m) => m.DomainsPage),
  },
  {
    path: 'logs',
    title: 'Logs - PiRouter',
    loadComponent: () => import('./pages/logs/logs').then((m) => m.LogsPage),
  },
  {
    path: 'diagnostics',
    title: 'Diagnostics - PiRouter',
    loadComponent: () => import('./pages/diagnostics/diagnostics').then((m) => m.DiagnosticsPage),
  },
  {
    path: 'system',
    title: 'System - PiRouter',
    loadComponent: () => import('./pages/system/system').then((m) => m.SystemPage),
  },
  { path: '**', redirectTo: 'dashboard' },
];
