import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, forkJoin } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';
import { Device, Employee, PermissionGrant, PermissionUpsert } from '../core/models';

interface PermissionForm {
  actionKey: string;
  allowed: boolean;
  scopeType: string;
  scopeId: string;
  priority: number;
  startsAtLocal: string;
  expiresAtLocal: string;
  reason: string;
  emergencyDeny: boolean;
}

function emptyForm(): PermissionForm {
  return { actionKey: 'screen.capture', allowed: false, scopeType: 'Global', scopeId: '*', priority: 100, startsAtLocal: '', expiresAtLocal: '', reason: '', emergencyDeny: false };
}

@Component({
  selector: 'app-permissions',
  imports: [FormsModule],
  template: `
    <section class="page-header"><div><p class="eyebrow">Effective authorization</p><h1>Permissions</h1><p>Create permanent, temporary, or emergency-deny decisions.</p></div><button class="button secondary" type="button" (click)="load()">Refresh</button></section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (success()) { <div class="alert success">{{ success() }}</div> }
    <section class="panel">
      <div class="panel-heading"><div><h2>{{ editingId() ? 'Edit permission' : 'Create permission' }}</h2><p class="muted">Emergency deny always wins. Higher priority wins among normal matching grants.</p></div></div>
      <form class="form-grid three" (ngSubmit)="save()">
        <label>Action<select name="actionKey" [(ngModel)]="form.actionKey">@for (action of actions(); track action) { <option [value]="action">{{ action }}</option> }</select></label>
        <label>Decision<select name="allowed" [(ngModel)]="form.allowed" [disabled]="form.emergencyDeny"><option [ngValue]="true">Allow</option><option [ngValue]="false">Block</option></select></label>
        <label>Priority<input name="priority" type="number" min="0" max="10000" [(ngModel)]="form.priority"></label>
        <label>Scope<select name="scopeType" [(ngModel)]="form.scopeType" (change)="scopeChanged()">@for (scope of scopes; track scope) { <option [value]="scope">{{ scope }}</option> }</select></label>
        @if (form.scopeType === 'Employee') {
          <label class="span-two">Employee<select name="scopeEmployee" [(ngModel)]="form.scopeId">@for (employee of employees(); track employee.id) { <option [value]="employee.id">{{ employee.displayName }} — {{ employee.employeeNumber }}</option> }</select></label>
        } @else if (form.scopeType === 'Device') {
          <label class="span-two">Device<select name="scopeDevice" [(ngModel)]="form.scopeId">@for (device of devices(); track device.id) { <option [value]="device.id">{{ device.machineName }}</option> }</select></label>
        } @else if (form.scopeType === 'Global') {
          <label class="span-two">Scope ID<input name="globalScope" value="*" disabled></label>
        } @else {
          <label class="span-two">Scope value<input name="scopeId" maxlength="300" required [(ngModel)]="form.scopeId" [placeholder]="scopePlaceholder()"></label>
        }
        <label>Starts at (optional)<input name="startsAt" type="datetime-local" [(ngModel)]="form.startsAtLocal"></label>
        <label>Expires at (optional)<input name="expiresAt" type="datetime-local" [(ngModel)]="form.expiresAtLocal"></label>
        <label class="checkbox-row"><input name="emergency" type="checkbox" [(ngModel)]="form.emergencyDeny" (change)="emergencyChanged()"> Emergency deny</label>
        <label class="span-three">Reason<textarea name="reason" maxlength="1000" rows="2" [(ngModel)]="form.reason"></textarea></label>
        <div class="form-actions span-three">
          @if (editingId()) { <button class="button ghost" type="button" (click)="reset()">Cancel</button> }
          <button class="button primary" type="submit" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save permission' }}</button>
        </div>
      </form>
    </section>
    <section class="panel table-panel">
      <div class="panel-heading"><div><h2>Permission history</h2><p class="muted">Expired and revoked grants remain visible for auditability.</p></div></div>
      <div class="table-wrap"><table><thead><tr><th>Action</th><th>Decision</th><th>Scope</th><th>Source / priority</th><th>Validity</th><th>Reason</th><th></th></tr></thead>
        <tbody>
          @for (grant of grants(); track grant.id) {
            <tr [class.muted-row]="grant.revokedAtUtc || isExpired(grant)">
              <td><strong>{{ grant.actionKey }}</strong><small>{{ grant.grantedBy }}</small></td>
              <td><span class="badge" [class.good]="grant.allowed" [class.bad]="!grant.allowed">{{ grant.allowed ? 'Allow' : 'Block' }}</span></td>
              <td>{{ grant.scopeType }}<small>{{ grant.scopeId }}</small></td>
              <td>{{ grant.source }}<small>Priority {{ grant.priority }}</small></td>
              <td>{{ grant.expiresAtUtc ? formatDate(grant.expiresAtUtc) : 'Permanent' }}<small>{{ grant.revokedAtUtc ? 'Revoked' : isExpired(grant) ? 'Expired' : 'Active' }}</small></td>
              <td>{{ grant.reason || '—' }}</td>
              <td class="actions-cell"><button class="button link" type="button" [disabled]="!!grant.revokedAtUtc" (click)="edit(grant)">Edit</button><button class="button danger link" type="button" [disabled]="!!grant.revokedAtUtc" (click)="revoke(grant)">Revoke</button></td>
            </tr>
          } @empty { <tr><td colspan="7" class="empty">No permission grants.</td></tr> }
        </tbody>
      </table></div>
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PermissionsComponent {
  private readonly api = inject(AdminApiService);
  readonly actions = signal<string[]>([]);
  readonly grants = signal<PermissionGrant[]>([]);
  readonly employees = signal<Employee[]>([]);
  readonly devices = signal<Device[]>([]);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly success = signal('');
  readonly scopes = ['Global', 'Employee', 'Device', 'Department', 'UserSid', 'Username', 'MachineName'];
  form: PermissionForm = emptyForm();

  constructor() { this.load(); }

  load(): void {
    this.error.set('');
    forkJoin({ actions: this.api.actions(), grants: this.api.permissions(), employees: this.api.employees(), devices: this.api.devices() }).subscribe({
      next: values => { this.actions.set(values.actions); this.grants.set(values.grants); this.employees.set(values.employees); this.devices.set(values.devices); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  scopeChanged(): void {
    if (this.form.scopeType === 'Global') this.form.scopeId = '*';
    else if (this.form.scopeType === 'Employee') this.form.scopeId = this.employees()[0]?.id ?? '';
    else if (this.form.scopeType === 'Device') this.form.scopeId = this.devices()[0]?.id ?? '';
    else this.form.scopeId = '';
  }

  emergencyChanged(): void { if (this.form.emergencyDeny) this.form.allowed = false; }

  save(): void {
    if (!this.form.actionKey || !this.form.scopeType || (!this.form.scopeId && this.form.scopeType !== 'Global')) return;
    this.saving.set(true); this.error.set(''); this.success.set('');
    const payload: PermissionUpsert = {
      actionKey: this.form.actionKey,
      allowed: this.form.emergencyDeny ? false : this.form.allowed,
      scopeType: this.form.scopeType,
      scopeId: this.form.scopeType === 'Global' ? '*' : this.form.scopeId.trim(),
      priority: this.form.priority,
      startsAtUtc: this.toIso(this.form.startsAtLocal),
      expiresAtUtc: this.toIso(this.form.expiresAtLocal),
      reason: this.form.reason.trim(),
      emergencyDeny: this.form.emergencyDeny
    };
    const request = this.editingId() ? this.api.updatePermission(this.editingId()!, payload) : this.api.createPermission(payload);
    request.pipe(finalize(() => this.saving.set(false))).subscribe({
      next: () => { this.success.set('Permission saved. The endpoint will refresh after its next heartbeat.'); this.reset(); this.load(); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  edit(grant: PermissionGrant): void {
    this.editingId.set(grant.id);
    this.form = {
      actionKey: grant.actionKey, allowed: grant.allowed, scopeType: grant.scopeType, scopeId: grant.scopeId,
      priority: grant.priority, startsAtLocal: this.toLocal(grant.startsAtUtc), expiresAtLocal: this.toLocal(grant.expiresAtUtc),
      reason: grant.reason, emergencyDeny: grant.source === 'EmergencyDeny'
    };
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  revoke(grant: PermissionGrant): void {
    if (!confirm(`Revoke permission ${grant.actionKey} for ${grant.scopeType}:${grant.scopeId}?`)) return;
    this.api.revokePermission(grant.id).subscribe({ next: () => { this.success.set('Permission revoked.'); this.load(); }, error: error => this.error.set(apiErrorMessage(error)) });
  }

  reset(): void { this.editingId.set(null); this.form = emptyForm(); }
  isExpired(grant: PermissionGrant): boolean { return grant.expiresAtUtc !== null && Date.parse(grant.expiresAtUtc) <= Date.now(); }
  formatDate(value: string): string { return new Date(value).toLocaleString(); }
  scopePlaceholder(): string { return this.form.scopeType === 'UserSid' ? 'S-1-5-21-…' : this.form.scopeType === 'Department' ? 'Finance' : 'Exact scope value'; }

  private toIso(value: string): string | null { return value ? new Date(value).toISOString() : null; }
  private toLocal(value: string | null): string {
    if (!value) return '';
    const date = new Date(value);
    const offset = date.getTimezoneOffset() * 60_000;
    return new Date(date.getTime() - offset).toISOString().slice(0, 16);
  }
}
