import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { AdminApiService } from '../core/admin-api.service';
import { apiErrorMessage } from '../core/api-error';

@Component({
  selector: 'app-policy',
  imports: [FormsModule],
  template: `
    <section class="page-header"><div><p class="eyebrow">Tenant baseline</p><h1>Base policy</h1><p>Edit enforcement settings. Permission grants are intentionally managed on their own page.</p></div><button class="button secondary" type="button" (click)="load()">Reload</button></section>
    @if (error()) { <div class="alert error">{{ error() }}</div> }
    @if (success()) { <div class="alert success">{{ success() }}</div> }
    <section class="panel">
      <div class="panel-heading"><div><h2>Policy JSON</h2><p class="muted">The API validates limits, canonicalizes action defaults, clears embedded grants, and overrides backend identity fields.</p></div><span class="badge">Policy {{ policyId() || 'loading' }}</span></div>
      <label class="code-editor">Validated JSON<textarea name="policyJson" spellcheck="false" [(ngModel)]="jsonText"></textarea></label>
      <div class="form-actions"><button class="button primary" type="button" [disabled]="saving()" (click)="save()">{{ saving() ? 'Saving…' : 'Validate and save' }}</button></div>
    </section>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PolicyComponent {
  private readonly api = inject(AdminApiService);
  readonly policyId = signal('');
  readonly saving = signal(false);
  readonly error = signal('');
  readonly success = signal('');
  jsonText = '';

  constructor() { this.load(); }

  load(): void {
    this.error.set('');
    this.api.policy().subscribe({
      next: response => { this.policyId.set(response.policyId); this.jsonText = JSON.stringify(response.policy, null, 2); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }

  save(): void {
    this.error.set(''); this.success.set('');
    let policy: Record<string, unknown>;
    try { policy = JSON.parse(this.jsonText) as Record<string, unknown>; }
    catch (error) { this.error.set(error instanceof Error ? `Invalid JSON: ${error.message}` : 'Invalid JSON.'); return; }
    this.saving.set(true);
    this.api.updatePolicy(policy).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: response => { this.success.set(`Policy saved as revision ${response.revision}.`); this.load(); },
      error: error => this.error.set(apiErrorMessage(error))
    });
  }
}
