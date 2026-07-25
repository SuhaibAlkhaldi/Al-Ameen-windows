import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, forkJoin } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';
import { Device, Employee, EnrollmentCodeResponse } from '../core/models';

@Component({
  selector: 'app-devices',
  imports: [FormsModule],
  template: `
    <section class="page-header"><div><p class="eyebrow">Endpoint management</p><h1>Devices & enrollment</h1><p>Enroll Windows agents, assign employees, and revoke compromised endpoints.</p></div><button class="button secondary" type="button" (click)="load()">Refresh</button></section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (success()) { <div class="alert success">{{ success() }}</div> }
    <section class="panel">
      <div class="panel-heading"><div><h2>Create one-time enrollment code</h2><p class="muted">The plaintext code is returned once and stored only as a hash.</p></div></div>
      <form class="inline-form" (ngSubmit)="createCode()">
        <label>Description<input name="description" maxlength="200" [(ngModel)]="description" placeholder="Amman finance laptop"></label>
        <label>Valid minutes<input name="minutes" type="number" min="5" max="1440" [(ngModel)]="validMinutes"></label>
        <button class="button primary" type="submit" [disabled]="creatingCode()">{{ creatingCode() ? 'Creating…' : 'Create code' }}</button>
      </form>
      @if (enrollmentCode(); as code) {
        <div class="secret-box"><div><small>One-time enrollment code</small><code>{{ code.enrollmentCode }}</code><span>Expires {{ formatDate(code.expiresAtUtc) }}</span></div><button class="button secondary" type="button" (click)="copyCode(code)">Copy</button></div>
      }
    </section>
    <section class="panel table-panel">
      <div class="panel-heading"><div><h2>Enrolled devices</h2><p class="muted">Assignments change the effective employee and department grants.</p></div></div>
      <div class="table-wrap"><table><thead><tr><th>Device</th><th>Agent</th><th>Employee assignment</th><th>Policy</th><th>Last seen</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (device of devices(); track device.id) {
            <tr>
              <td><strong>{{ device.machineName }}</strong><small>{{ device.osVersion || 'OS not reported' }}</small></td>
              <td>{{ device.agentVersion || '—' }}<small>{{ device.pendingAuditEventCount }} pending events</small></td>
              <td>
                <select [name]="'employee-' + device.id" [(ngModel)]="device.employeeId" (change)="assign(device)">
                  <option [ngValue]="null">Unassigned</option>
                  @for (employee of employees(); track employee.id) { <option [ngValue]="employee.id">{{ employee.displayName }}</option> }
                </select>
              </td>
              <td>v{{ device.lastAppliedPolicyVersion }}</td><td>{{ formatDate(device.lastSeenAtUtc) }}</td>
              <td><span class="badge" [class.good]="device.isActive" [class.bad]="!device.isActive">{{ device.isActive ? 'Active' : 'Revoked' }}</span></td>
              <td><button class="button danger link" type="button" [disabled]="!device.isActive" (click)="revoke(device)">Revoke</button></td>
            </tr>
          } @empty { <tr><td colspan="7" class="empty">No enrolled devices.</td></tr> }
        </tbody>
      </table></div>
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DevicesComponent {
  private readonly api = inject(AdminApiService);
  readonly devices = signal<Device[]>([]);
  readonly employees = signal<Employee[]>([]);
  readonly enrollmentCode = signal<EnrollmentCodeResponse | null>(null);
  readonly creatingCode = signal(false);
  readonly error = signal('');
  readonly success = signal('');
  description = '';
  validMinutes = 30;

  constructor() { this.load(); }

  load(): void {
    this.error.set('');
    forkJoin({ devices: this.api.devices(), employees: this.api.employees() }).subscribe({
      next: values => { this.devices.set(values.devices); this.employees.set(values.employees.filter(value => value.isActive)); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  createCode(): void {
    this.creatingCode.set(true); this.error.set('');
    this.api.createEnrollmentCode(this.description.trim(), this.validMinutes)
      .pipe(finalize(() => this.creatingCode.set(false)))
      .subscribe({ next: value => this.enrollmentCode.set(value), error: error => this.error.set(apiErrorMessage(error)) });
  }

  assign(device: Device): void {
    this.error.set(''); this.success.set('');
    this.api.assignDevice(device.id, device.employeeId).subscribe({
      next: () => { this.success.set(`Assignment updated for ${device.machineName}.`); this.load(); },
      error: error => { this.error.set(apiErrorMessage(error)); this.load(); }
    });
  }

  revoke(device: Device): void {
    if (!confirm(`Revoke ${device.machineName}? The endpoint token will stop working immediately.`)) return;
    this.api.revokeDevice(device.id).subscribe({
      next: () => { this.success.set(`${device.machineName} was revoked.`); this.load(); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  copyCode(code: EnrollmentCodeResponse): void { void navigator.clipboard.writeText(code.enrollmentCode); }
  formatDate(value: string | null): string { return value ? new Date(value).toLocaleString() : 'Never'; }
}
