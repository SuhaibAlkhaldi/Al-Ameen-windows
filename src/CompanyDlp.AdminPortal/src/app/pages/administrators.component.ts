import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';
import { AuthService } from '../core/auth.service';
import { AdminUser, AdminUserCreate, AdminUserUpdate } from '../core/models';

type AdminRole = 'Owner' | 'PolicyAdmin' | 'Auditor';

interface AdminForm {
  email: string;
  displayName: string;
  password: string;
  role: AdminRole;
  isActive: boolean;
}

function emptyForm(): AdminForm {
  return { email: '', displayName: '', password: '', role: 'PolicyAdmin', isActive: true };
}

@Component({
  selector: 'app-administrators',
  imports: [FormsModule],
  template: `
    <section class="page-header"><div><p class="eyebrow">Role-based access</p><h1>Administrators</h1><p>Create owners, policy administrators, and read-only auditors.</p></div><button class="button secondary" type="button" (click)="load()">Refresh</button></section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (success()) { <div class="alert success">{{ success() }}</div> }
    <section class="panel">
      <div class="panel-heading"><div><h2>{{ editingId() ? 'Edit administrator' : 'Add administrator' }}</h2><p class="muted">Passwords are never returned by the API. Leave the password empty while editing to keep it unchanged.</p></div></div>
      <form class="form-grid three" (ngSubmit)="save()">
        <label>Email<input name="email" type="email" maxlength="320" required [disabled]="!!editingId()" [(ngModel)]="form.email"></label>
        <label>Display name<input name="displayName" maxlength="200" required [(ngModel)]="form.displayName"></label>
        <label>Role<select name="role" [(ngModel)]="form.role">@for (role of roles; track role) { <option [value]="role">{{ role }}</option> }</select></label>
        <label class="span-two">{{ editingId() ? 'New password (optional)' : 'Temporary password' }}<input name="password" type="password" minlength="12" maxlength="1024" [required]="!editingId()" [(ngModel)]="form.password"></label>
        <label class="checkbox-row"><input name="active" type="checkbox" [(ngModel)]="form.isActive"> Active account</label>
        <div class="form-actions span-three">
          @if (editingId()) { <button class="button ghost" type="button" (click)="reset()">Cancel</button> }
          <button class="button primary" type="submit" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save administrator' }}</button>
        </div>
      </form>
    </section>
    <section class="panel table-panel">
      <div class="panel-heading"><div><h2>Tenant administrators</h2><p class="muted">At least one active Owner is always required.</p></div></div>
      <div class="table-wrap"><table><thead><tr><th>Administrator</th><th>Role</th><th>Created</th><th>Last login</th><th>Status</th><th></th></tr></thead><tbody>
        @for (admin of admins(); track admin.id) {
          <tr><td><strong>{{ admin.displayName }}</strong><small>{{ admin.email }}</small></td><td>{{ admin.role }}</td><td>{{ formatDate(admin.createdAtUtc) }}</td><td>{{ formatDate(admin.lastLoginAtUtc) }}</td><td><span class="badge" [class.good]="admin.isActive" [class.bad]="!admin.isActive">{{ admin.isActive ? 'Active' : 'Inactive' }}</span></td><td><button class="button link" type="button" (click)="edit(admin)">Edit</button></td></tr>
        } @empty { <tr><td colspan="6" class="empty">No administrator accounts.</td></tr> }
      </tbody></table></div>
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AdministratorsComponent {
  private readonly api = inject(AdminApiService);
  private readonly auth = inject(AuthService);
  readonly admins = signal<AdminUser[]>([]);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly success = signal('');
  readonly roles: AdminRole[] = ['Owner', 'PolicyAdmin', 'Auditor'];
  form: AdminForm = emptyForm();

  constructor() { this.load(); }

  load(): void {
    if (this.auth.session()?.role !== 'Owner') return;
    this.api.adminUsers().subscribe({ next: values => this.admins.set(values), error: error => this.error.set(apiErrorMessage(error)) });
  }

  edit(admin: AdminUser): void {
    this.editingId.set(admin.id);
    this.form = { email: admin.email, displayName: admin.displayName, password: '', role: admin.role, isActive: admin.isActive };
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  reset(): void { this.editingId.set(null); this.form = emptyForm(); }

  save(): void {
    if (!this.form.displayName.trim() || (!this.editingId() && (!this.form.email.trim() || this.form.password.length < 12))) return;
    this.saving.set(true); this.error.set(''); this.success.set('');
    const id = this.editingId();
    const request = id
      ? this.api.updateAdminUser(id, {
          displayName: this.form.displayName.trim(), role: this.form.role, isActive: this.form.isActive,
          newPassword: this.form.password ? this.form.password : null
        } satisfies AdminUserUpdate)
      : this.api.createAdminUser({
          email: this.form.email.trim(), displayName: this.form.displayName.trim(), password: this.form.password, role: this.form.role
        } satisfies AdminUserCreate);
    request.pipe(finalize(() => this.saving.set(false))).subscribe({
      next: () => { this.success.set('Administrator account saved.'); this.reset(); this.load(); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  formatDate(value: string | null): string { return value ? new Date(value).toLocaleString() : 'Never'; }
}
