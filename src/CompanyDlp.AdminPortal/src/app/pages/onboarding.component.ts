import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { apiErrorMessage } from '../core/api-error';

@Component({
  selector: 'app-onboarding',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="auth-page">
      <form class="auth-card wide" (ngSubmit)="submit()">
        <div class="brand auth-brand"><span class="brand-mark">DLP</span><div><strong>Al-Ameen</strong><small>Secure onboarding</small></div></div>
        <h1>Create the first tenant owner</h1>
        <p class="muted">Public onboarding is intended for initial development setup only.</p>
        @if (error()) { <div class="alert error">{{ error() }}</div> }
        <div class="form-grid two">
          <label>Company name<input name="tenantName" required maxlength="200" [(ngModel)]="tenantName"></label>
          <label>Administrator name<input name="displayName" required maxlength="200" [(ngModel)]="displayName"></label>
          <label>Email<input name="email" type="email" autocomplete="username" required maxlength="320" [(ngModel)]="email"></label>
          <label>Password<input name="password" type="password" autocomplete="new-password" minlength="12" maxlength="1024" required [(ngModel)]="password"></label>
        </div>
        <button class="button primary full" type="submit" [disabled]="loading()">{{ loading() ? 'Creating…' : 'Create tenant' }}</button>
        <p class="auth-link">Already configured? <a routerLink="/login">Sign in</a></p>
      </form>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class OnboardingComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  tenantName = '';
  displayName = '';
  email = '';
  password = '';
  readonly loading = signal(false);
  readonly error = signal('');

  submit(): void {
    if (!this.tenantName.trim() || !this.displayName.trim() || !this.email.trim() || this.password.length < 12) return;
    this.loading.set(true);
    this.error.set('');
    this.auth.onboard({
      tenantName: this.tenantName.trim(),
      adminDisplayName: this.displayName.trim(),
      email: this.email.trim(),
      password: this.password
    }).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: () => void this.router.navigateByUrl('/dashboard'),
      error: error => this.error.set(apiErrorMessage(error))
    });
  }
}
