import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { apiErrorMessage } from '../core/api-error';

@Component({
  selector: 'app-login',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="auth-page">
      <form class="auth-card" (ngSubmit)="submit()">
        <div class="brand auth-brand"><span class="brand-mark">DLP</span><div><strong>Al-Ameen</strong><small>Admin Portal</small></div></div>
        <h1>Administrator login</h1>
        <p class="muted">Manage endpoint permissions and security policy.</p>
        @if (error()) { <div class="alert error">{{ error() }}</div> }
        <label>Email<input name="email" type="email" autocomplete="username" required maxlength="320" [(ngModel)]="email"></label>
        <label>Password<input name="password" type="password" autocomplete="current-password" required maxlength="1024" [(ngModel)]="password"></label>
        <button class="button primary full" type="submit" [disabled]="loading()">{{ loading() ? 'Signing in…' : 'Sign in' }}</button>
        <p class="auth-link">First setup? <a routerLink="/onboarding">Create the tenant owner</a></p>
      </form>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  email = '';
  password = '';
  readonly loading = signal(false);
  readonly error = signal('');

  submit(): void {
    if (!this.email.trim() || !this.password) return;
    this.loading.set(true);
    this.error.set('');
    this.auth.login({ email: this.email.trim(), password: this.password })
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: () => void this.router.navigateByUrl('/dashboard'),
        error: error => this.error.set(apiErrorMessage(error))
      });
  }
}
