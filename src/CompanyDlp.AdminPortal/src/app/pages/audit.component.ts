import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';
import { AdminAuditEvent, AuditEvent } from '../core/models';

@Component({
  selector: 'app-audit',
  template: `
    <section class="page-header"><div><p class="eyebrow">Forensics & accountability</p><h1>Audit</h1><p>Endpoint decisions and administrator changes are stored separately.</p></div><button class="button secondary" type="button" (click)="load()">Refresh</button></section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    <div class="tabs"><button type="button" [class.active]="tab() === 'endpoint'" (click)="tab.set('endpoint')">Endpoint events</button><button type="button" [class.active]="tab() === 'admin'" (click)="tab.set('admin')">Administrator audit</button></div>
    @if (tab() === 'endpoint') {
      <section class="panel table-panel"><div class="table-wrap"><table><thead><tr><th>Time</th><th>Action</th><th>Decision</th><th>Event</th><th>Reason</th><th>Device</th></tr></thead><tbody>
        @for (event of endpointEvents(); track event.eventId) { <tr><td>{{ formatDate(event.occurredAtUtc) }}</td><td><strong>{{ event.actionKey }}</strong></td><td><span class="badge" [class.good]="event.decision === 'Allow'" [class.bad]="event.decision === 'Block'">{{ event.decision }}</span></td><td>{{ event.eventType }}</td><td>{{ event.reasonCode }}</td><td><code>{{ shortId(event.deviceId) }}</code></td></tr> }
        @empty { <tr><td colspan="6" class="empty">No endpoint events.</td></tr> }
      </tbody></table></div></section>
    } @else {
      <section class="panel table-panel"><div class="table-wrap"><table><thead><tr><th>Time</th><th>Administrator</th><th>Action</th><th>Target</th><th>IP address</th><th>Details</th></tr></thead><tbody>
        @for (event of adminEvents(); track event.id) { <tr><td>{{ formatDate(event.occurredAtUtc) }}</td><td>{{ event.adminEmail }}</td><td><strong>{{ event.action }}</strong></td><td>{{ event.targetType }}<small>{{ event.targetId }}</small></td><td>{{ event.ipAddress || '—' }}</td><td><code class="details-code">{{ compactDetails(event.detailsJson) }}</code></td></tr> }
        @empty { <tr><td colspan="6" class="empty">No administrator audit records.</td></tr> }
      </tbody></table></div></section>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AuditComponent {
  private readonly api = inject(AdminApiService);
  readonly tab = signal<'endpoint' | 'admin'>('endpoint');
  readonly endpointEvents = signal<AuditEvent[]>([]);
  readonly adminEvents = signal<AdminAuditEvent[]>([]);
  readonly error = signal('');

  constructor() { this.load(); }

  load(): void {
    this.error.set('');
    forkJoin({ endpoint: this.api.auditEvents(), admin: this.api.adminAudit() }).subscribe({
      next: values => { this.endpointEvents.set(values.endpoint); this.adminEvents.set(values.admin); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  formatDate(value: string): string { return new Date(value).toLocaleString(); }
  shortId(value: string): string { return value.slice(0, 8); }
  compactDetails(value: string): string { return value.length > 140 ? `${value.slice(0, 140)}…` : value; }
}
