import { Routes } from '@angular/router';
import { DashboardComponent } from './dashboard/dashboard.component';
import { RoutingComponent } from './routing/routing.component';
import { SystemComponent } from './system/system.component';
import { VpnComponent } from './vpn/vpn.component';

export const routes: Routes = [
  { path: '', redirectTo: '/dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: DashboardComponent },
  { path: 'vpn', component: VpnComponent },
  { path: 'routing', component: RoutingComponent },
  { path: 'system', component: SystemComponent }
];