import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="app-shell">
      <aside class="sidebar">
        <div class="brand">
          <span class="brand-mark">DLP</span>
          <div><strong>Al-Ameen</strong><small>Central Administration</small></div>
        </div>
        <nav aria-label="Administration">
          @if (auth.session()?.role !== 'Auditor') {
            <a routerLink="/dashboard" routerLinkActive="active">Dashboard</a>
            @if (auth.session()?.role === 'Owner') { <a routerLink="/administrators" routerLinkActive="active">Administrators</a> }
            <a routerLink="/employees" routerLinkActive="active">Employees</a>
            <a routerLink="/devices" routerLinkActive="active">Devices & Enrollment</a>
            <a routerLink="/permissions" routerLinkActive="active">Permissions</a>
            <a routerLink="/policy" routerLinkActive="active">Base Policy</a>
          }
          <a routerLink="/audit" routerLinkActive="active">Audit</a>
        </nav>
        <div class="sidebar-user">
          <span>{{ auth.session()?.displayName }}</span>
          <small>{{ auth.session()?.role }}</small>
          <button type="button" class="button ghost" (click)="auth.logout()">Sign out</button>
        </div>
      </aside>
      <main class="main-content"><router-outlet /></main>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  readonly auth = inject(AuthService);
}
