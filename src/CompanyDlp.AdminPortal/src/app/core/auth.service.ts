import { computed, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import { LoginRequest, LoginResponse, OnboardingRequest } from './models';

const STORAGE_KEY = 'company-dlp-admin-session';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly sessionSignal = signal<LoginResponse | null>(this.readStoredSession());

  readonly session = this.sessionSignal.asReadonly();
  readonly isAuthenticated = computed(() => {
    const value = this.sessionSignal();
    return value !== null && Date.parse(value.expiresAtUtc) > Date.now();
  });

  login(request: LoginRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiBaseUrl}/api/v1/admin/auth/login`, request)
      .pipe(tap(response => this.saveSession(response)));
  }

  onboard(request: OnboardingRequest): Observable<LoginResponse> {
    return this.http.post<LoginResponse>(`${environment.apiBaseUrl}/api/v1/admin/onboarding/register`, request)
      .pipe(tap(response => this.saveSession(response)));
  }

  token(): string | null {
    return this.isAuthenticated() ? this.sessionSignal()?.accessToken ?? null : null;
  }

  logout(): void {
    sessionStorage.removeItem(STORAGE_KEY);
    this.sessionSignal.set(null);
    void this.router.navigateByUrl('/login');
  }

  private saveSession(response: LoginResponse): void {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(response));
    this.sessionSignal.set(response);
  }

  private readStoredSession(): LoginResponse | null {
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const value = JSON.parse(raw) as LoginResponse;
      if (!value.accessToken || Date.parse(value.expiresAtUtc) <= Date.now()) {
        sessionStorage.removeItem(STORAGE_KEY);
        return null;
      }
      return value;
    } catch {
      sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }
  }
}
