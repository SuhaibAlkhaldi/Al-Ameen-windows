import { Routes } from '@angular/router';
import { authGuard, ownerGuard, policyAdminGuard } from './core/auth.guard';
import { LoginComponent } from './pages/login.component';
import { OnboardingComponent } from './pages/onboarding.component';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Login | Company DLP' },
  { path: 'onboarding', component: OnboardingComponent, title: 'Onboarding | Company DLP' },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/dashboard.component').then(m => m.DashboardComponent), title: 'Dashboard | Company DLP' },
      { path: 'administrators', canActivate: [ownerGuard], loadComponent: () => import('./pages/administrators.component').then(m => m.AdministratorsComponent), title: 'Administrators | Company DLP' },
      { path: 'employees', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/employees.component').then(m => m.EmployeesComponent), title: 'Employees | Company DLP' },
      { path: 'devices', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/devices.component').then(m => m.DevicesComponent), title: 'Devices | Company DLP' },
      { path: 'permissions', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/permissions.component').then(m => m.PermissionsComponent), title: 'Permissions | Company DLP' },
      { path: 'policy', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/policy.component').then(m => m.PolicyComponent), title: 'Policy | Company DLP' },
      { path: 'audit', loadComponent: () => import('./pages/audit.component').then(m => m.AuditComponent), title: 'Audit | Company DLP' }
    ]
  },
  { path: '**', redirectTo: '' }
];
