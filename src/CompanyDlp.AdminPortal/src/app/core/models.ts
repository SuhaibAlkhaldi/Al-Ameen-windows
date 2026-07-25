export interface LoginRequest {
  email: string;
  password: string;
}

export interface OnboardingRequest extends LoginRequest {
  tenantName: string;
  adminDisplayName: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresAtUtc: string;
  tenantId: string;
  adminUserId: string;
  role: string;
  displayName: string;
}

export interface Employee {
  id: string;
  employeeNumber: string;
  displayName: string;
  username: string;
  windowsSid: string;
  department: string;
  isActive: boolean;
  deviceCount: number;
}

export interface EmployeeUpsert {
  employeeNumber: string;
  displayName: string;
  username: string;
  windowsSid: string;
  department: string;
  isActive: boolean;
}

export interface Device {
  id: string;
  machineName: string;
  agentVersion: string;
  osVersion: string;
  employeeId: string | null;
  employeeName: string | null;
  isActive: boolean;
  enrolledAtUtc: string;
  lastSeenAtUtc: string | null;
  lastAppliedPolicyVersion: number;
  pendingAuditEventCount: number;
  tokenExpiresAtUtc: string | null;
}

export interface PermissionGrant {
  id: string;
  actionKey: string;
  allowed: boolean;
  scopeType: string;
  scopeId: string;
  source: string;
  priority: number;
  startsAtUtc: string;
  expiresAtUtc: string | null;
  reason: string;
  grantedBy: string;
  createdAtUtc: string;
  revokedAtUtc: string | null;
}

export interface PermissionUpsert {
  actionKey: string;
  allowed: boolean;
  scopeType: string;
  scopeId: string;
  priority: number;
  startsAtUtc?: string | null;
  expiresAtUtc?: string | null;
  reason: string;
  emergencyDeny: boolean;
}

export interface EnrollmentCodeResponse {
  id: string;
  enrollmentCode: string;
  expiresAtUtc: string;
}

export interface TenantPolicyResponse {
  policyId: string;
  updatedAtUtc: string;
  policy: Record<string, unknown>;
}

export interface AuditEvent {
  eventId: string;
  deviceId: string;
  correlationId: string;
  userId: string | null;
  actionKey: string;
  eventType: string;
  decision: string;
  reasonCode: string;
  occurredAtUtc: string;
  receivedAtUtc: string;
}

export interface AdminAuditEvent {
  id: number;
  adminEmail: string;
  action: string;
  targetType: string;
  targetId: string;
  detailsJson: string;
  ipAddress: string;
  occurredAtUtc: string;
}

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  role: 'Owner' | 'PolicyAdmin' | 'Auditor';
  isActive: boolean;
  createdAtUtc: string;
  lastLoginAtUtc: string | null;
}

export interface AdminUserCreate {
  email: string;
  displayName: string;
  password: string;
  role: 'Owner' | 'PolicyAdmin' | 'Auditor';
}

export interface AdminUserUpdate {
  displayName: string;
  role: 'Owner' | 'PolicyAdmin' | 'Auditor';
  isActive: boolean;
  newPassword: string | null;
}
