import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AdminAuditEvent, AdminUser, AdminUserCreate, AdminUserUpdate, AuditEvent, Device, Employee, EmployeeUpsert, EnrollmentCodeResponse,
  PermissionGrant, PermissionUpsert, TenantPolicyResponse
} from './models';

@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/api/v1/admin`;

  adminUsers(): Observable<AdminUser[]> { return this.http.get<AdminUser[]>(`${this.base}/admin-users`); }
  createAdminUser(value: AdminUserCreate): Observable<AdminUser> { return this.http.post<AdminUser>(`${this.base}/admin-users`, value); }
  updateAdminUser(id: string, value: AdminUserUpdate): Observable<AdminUser> { return this.http.put<AdminUser>(`${this.base}/admin-users/${id}`, value); }

  actions(): Observable<string[]> { return this.http.get<string[]>(`${this.base}/actions`); }
  employees(): Observable<Employee[]> { return this.http.get<Employee[]>(`${this.base}/employees`); }
  createEmployee(value: EmployeeUpsert): Observable<Employee> { return this.http.post<Employee>(`${this.base}/employees`, value); }
  updateEmployee(id: string, value: EmployeeUpsert): Observable<Employee> { return this.http.put<Employee>(`${this.base}/employees/${id}`, value); }

  devices(): Observable<Device[]> { return this.http.get<Device[]>(`${this.base}/devices`); }
  assignDevice(id: string, employeeId: string | null): Observable<void> {
    return this.http.put<void>(`${this.base}/devices/${id}/assignment`, { employeeId });
  }
  revokeDevice(id: string): Observable<void> { return this.http.post<void>(`${this.base}/devices/${id}/revoke`, {}); }
  createEnrollmentCode(description: string, validForMinutes: number): Observable<EnrollmentCodeResponse> {
    return this.http.post<EnrollmentCodeResponse>(`${this.base}/enrollment-codes`, { description, validForMinutes });
  }

  permissions(): Observable<PermissionGrant[]> { return this.http.get<PermissionGrant[]>(`${this.base}/permissions`); }
  createPermission(value: PermissionUpsert): Observable<PermissionGrant> { return this.http.post<PermissionGrant>(`${this.base}/permissions`, value); }
  updatePermission(id: string, value: PermissionUpsert): Observable<PermissionGrant> { return this.http.put<PermissionGrant>(`${this.base}/permissions/${id}`, value); }
  revokePermission(id: string): Observable<void> { return this.http.delete<void>(`${this.base}/permissions/${id}`); }

  policy(): Observable<TenantPolicyResponse> { return this.http.get<TenantPolicyResponse>(`${this.base}/policy`); }
  updatePolicy(policy: Record<string, unknown>): Observable<{ policyId: string; revision: number; updatedAtUtc: string }> {
    return this.http.put<{ policyId: string; revision: number; updatedAtUtc: string }>(`${this.base}/policy`, { policy });
  }

  auditEvents(take = 250): Observable<AuditEvent[]> { return this.http.get<AuditEvent[]>(`${this.base}/audit-events?take=${take}`); }
  adminAudit(take = 250): Observable<AdminAuditEvent[]> { return this.http.get<AdminAuditEvent[]>(`${this.base}/admin-audit?take=${take}`); }
}
