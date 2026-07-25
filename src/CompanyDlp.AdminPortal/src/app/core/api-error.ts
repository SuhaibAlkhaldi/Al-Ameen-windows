import { HttpErrorResponse } from '@angular/common/http';

export function apiErrorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const apiError = error.error?.error;
    if (typeof apiError === 'string' && apiError.length > 0) return apiError;
    if (typeof error.error?.title === 'string') return error.error.title;
    if (error.status === 0) return 'The Admin API is unavailable.';
    return `${error.status} ${error.statusText}`.trim();
  }
  return error instanceof Error ? error.message : 'Unexpected error.';
}
