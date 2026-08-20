import { Routes } from '@angular/router';
import { authGuard, ownerGuard, policyAdminGuard } from './core/auth.guard';
import { LoginComponent } from './pages/login.component';
import { OnboardingComponent } from './pages/onboarding.component';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Login | Al-Ameen' },
  { path: 'onboarding', component: OnboardingComponent, title: 'Onboarding | Al-Ameen' },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/dashboard.component').then(m => m.DashboardComponent), title: 'Dashboard | Al-Ameen' },
      { path: 'administrators', canActivate: [ownerGuard], loadComponent: () => import('./pages/administrators.component').then(m => m.AdministratorsComponent), title: 'Administrators | Al-Ameen' },
      { path: 'employees', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/employees.component').then(m => m.EmployeesComponent), title: 'Employees | Al-Ameen' },
      { path: 'devices', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/devices.component').then(m => m.DevicesComponent), title: 'Devices | Al-Ameen' },
      { path: 'permissions', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/permissions.component').then(m => m.PermissionsComponent), title: 'Permissions | Al-Ameen' },
      { path: 'policy', canActivate: [policyAdminGuard], loadComponent: () => import('./pages/policy.component').then(m => m.PolicyComponent), title: 'Policy | Al-Ameen' },
      { path: 'audit', loadComponent: () => import('./pages/audit.component').then(m => m.AuditComponent), title: 'Audit | Al-Ameen' }
    ]
  },
  { path: '**', redirectTo: '' }
];
