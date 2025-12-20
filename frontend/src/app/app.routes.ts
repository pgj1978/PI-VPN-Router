import { Routes } from '@angular/router';
import { DashboardComponent } from './dashboard/dashboard.component';
import { RoutingComponent } from './routing/routing.component';

export const routes: Routes = [
  { path: '', component: DashboardComponent },
  { path: 'routing', component: RoutingComponent },
  { path: '**', redirectTo: '' }
];