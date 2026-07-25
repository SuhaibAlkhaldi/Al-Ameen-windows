import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';

@Component({
  selector: 'app-dashboard',
  template: `
    <section class="page-header">
      <div><p class="eyebrow">Central control plane</p><h1>Security overview</h1><p>Current tenant devices, identities, grants, and audit activity.</p></div>
      <button class="button secondary" type="button" (click)="load()">Refresh</button>
    </section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    <section class="stats-grid">
      <article class="stat-card"><span>Employees</span><strong>{{ employees() }}</strong><small>Managed identities</small></article>
      <article class="stat-card"><span>Active devices</span><strong>{{ activeDevices() }}</strong><small>{{ totalDevices() }} enrolled total</small></article>
      <article class="stat-card"><span>Active grants</span><strong>{{ activeGrants() }}</strong><small>{{ temporaryGrants() }} temporary</small></article>
      <article class="stat-card"><span>Pending endpoint events</span><strong>{{ pendingEvents() }}</strong><small>Awaiting synchronization</small></article>
    </section>
    <section class="panel">
      <div class="panel-heading"><div><h2>How changes reach Windows</h2><p class="muted">No direct inbound connection to employee laptops is required.</p></div></div>
      <div class="flow">
        <span>Admin Portal</span><b>→</b><span>Admin API + SQL Server</span><b>→</b><span>Policy revision</span><b>→</b><span>Agent heartbeat</span><b>→</b><span>Signed device policy</span>
      </div>
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DashboardComponent {
  private readonly api = inject(AdminApiService);
  readonly employees = signal(0);
  readonly activeDevices = signal(0);
  readonly totalDevices = signal(0);
  readonly activeGrants = signal(0);
  readonly temporaryGrants = signal(0);
  readonly pendingEvents = signal(0);
  readonly error = signal('');

  constructor() { this.load(); }

  load(): void {
    this.error.set('');
    forkJoin({ employees: this.api.employees(), devices: this.api.devices(), grants: this.api.permissions() }).subscribe({
      next: result => {
        const now = Date.now();
        const active = result.grants.filter(value => !value.revokedAtUtc && (!value.expiresAtUtc || Date.parse(value.expiresAtUtc) > now));
        this.employees.set(result.employees.filter(value => value.isActive).length);
        this.totalDevices.set(result.devices.length);
        this.activeDevices.set(result.devices.filter(value => value.isActive).length);
        this.activeGrants.set(active.length);
        this.temporaryGrants.set(active.filter(value => value.expiresAtUtc !== null).length);
        this.pendingEvents.set(result.devices.reduce((total, value) => total + value.pendingAuditEventCount, 0));
      },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }
}
