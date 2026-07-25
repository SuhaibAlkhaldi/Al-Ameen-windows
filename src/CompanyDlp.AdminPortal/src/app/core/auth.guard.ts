import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? true : inject(Router).createUrlTree(['/login']);
};

export const ownerGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() && auth.session()?.role === 'Owner'
    ? true
    : inject(Router).createUrlTree(['/dashboard']);
};

export const policyAdminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const role = auth.session()?.role;
  return auth.isAuthenticated() && (role === 'Owner' || role === 'PolicyAdmin')
    ? true
    : inject(Router).createUrlTree(['/audit']);
};
