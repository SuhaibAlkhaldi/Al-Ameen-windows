import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';
import { Employee, EmployeeUpsert } from '../core/models';

function emptyEmployee(): EmployeeUpsert {
  return { employeeNumber: '', displayName: '', username: '', windowsSid: '', department: '', isActive: true };
}

@Component({
  selector: 'app-employees',
  imports: [FormsModule],
  template: `
    <section class="page-header"><div><p class="eyebrow">Identity management</p><h1>Employees</h1><p>Link Windows identities and departments to enrolled devices.</p></div></section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (success()) { <div class="alert success">{{ success() }}</div> }
    <section class="panel">
      <div class="panel-heading"><div><h2>{{ editingId() ? 'Edit employee' : 'Add employee' }}</h2><p class="muted">SID is preferred; username is used as a fallback.</p></div></div>
      <form class="form-grid three" (ngSubmit)="save()">
        <label>Employee number<input name="employeeNumber" maxlength="100" required [(ngModel)]="form.employeeNumber"></label>
        <label>Display name<input name="displayName" maxlength="200" required [(ngModel)]="form.displayName"></label>
        <label>Department<input name="department" maxlength="200" [(ngModel)]="form.department"></label>
        <label>Windows username<input name="username" maxlength="256" [(ngModel)]="form.username"></label>
        <label class="span-two">Windows SID<input name="windowsSid" maxlength="256" placeholder="S-1-5-21-…" [(ngModel)]="form.windowsSid"></label>
        <label class="checkbox-row"><input name="isActive" type="checkbox" [(ngModel)]="form.isActive"> Active employee</label>
        <div class="form-actions span-three">
          @if (editingId()) { <button class="button ghost" type="button" (click)="cancelEdit()">Cancel</button> }
          <button class="button primary" type="submit" [disabled]="saving()">{{ saving() ? 'Saving…' : 'Save employee' }}</button>
        </div>
      </form>
    </section>
    <section class="panel table-panel">
      <div class="panel-heading"><div><h2>Managed employees</h2><p class="muted">{{ employees().length }} records</p></div><button class="button secondary" type="button" (click)="load()">Refresh</button></div>
      <div class="table-wrap"><table><thead><tr><th>Employee</th><th>Windows identity</th><th>Department</th><th>Devices</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (employee of employees(); track employee.id) {
            <tr>
              <td><strong>{{ employee.displayName }}</strong><small>{{ employee.employeeNumber }}</small></td>
              <td><span>{{ employee.username || '—' }}</span><small>{{ employee.windowsSid || 'No SID' }}</small></td>
              <td>{{ employee.department || '—' }}</td><td>{{ employee.deviceCount }}</td>
              <td><span class="badge" [class.good]="employee.isActive" [class.bad]="!employee.isActive">{{ employee.isActive ? 'Active' : 'Inactive' }}</span></td>
              <td><button class="button link" type="button" (click)="edit(employee)">Edit</button></td>
            </tr>
          } @empty { <tr><td colspan="6" class="empty">No employees yet.</td></tr> }
        </tbody>
      </table></div>
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class EmployeesComponent {
  private readonly api = inject(AdminApiService);
  readonly employees = signal<Employee[]>([]);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly error = signal('');
  readonly success = signal('');
  form: EmployeeUpsert = emptyEmployee();

  constructor() { this.load(); }

  load(): void {
    this.api.employees().subscribe({ next: values => this.employees.set(values), error: error => this.error.set(apiErrorMessage(error)) });
  }

  edit(employee: Employee): void {
    this.editingId.set(employee.id);
    this.form = {
      employeeNumber: employee.employeeNumber, displayName: employee.displayName, username: employee.username,
      windowsSid: employee.windowsSid, department: employee.department, isActive: employee.isActive
    };
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  cancelEdit(): void { this.editingId.set(null); this.form = emptyEmployee(); }

  save(): void {
    if (!this.form.employeeNumber.trim() || !this.form.displayName.trim()) return;
    this.saving.set(true); this.error.set(''); this.success.set('');
    const payload: EmployeeUpsert = {
      ...this.form,
      employeeNumber: this.form.employeeNumber.trim(), displayName: this.form.displayName.trim(),
      username: this.form.username.trim(), windowsSid: this.form.windowsSid.trim(), department: this.form.department.trim()
    };
    const request = this.editingId()
      ? this.api.updateEmployee(this.editingId()!, payload)
      : this.api.createEmployee(payload);
    request.pipe(finalize(() => this.saving.set(false))).subscribe({
      next: () => { this.success.set('Employee saved and the tenant policy revision was updated.'); this.cancelEdit(); this.load(); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }
}
