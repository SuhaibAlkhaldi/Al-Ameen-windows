# تقرير تنفيذ Company DLP v1.1.0 — الإدارة المركزية

## القرار المعماري

تمت قراءة نسخة `v1.0.10` أولًا. النسخة كانت تحتوي أصلًا على معظم Endpoint Enforcement الصحيح: عقود Policy وHeartbeat وAudit، تقييم Allow/Block، صلاحيات مؤقتة، Cache محمية، وتوقيع Policy. لذلك لم تتم إعادة كتابة الحماية على Windows.

تم بناء طبقة الإدارة المركزية فوق العقود الموجودة:

```text
Admin Portal → Admin API → SQL Server → Policy Revision
                                         ↓
Windows Heartbeat ← Device Policy Snapshot موقعة ومخصصة للجهاز
```

هذا يمنع تحويل الـAPI إلى Remote Command Executor، ويجعل قاعدة البيانات والـPolicy الموقعة هما مصدر القرار المركزي.

## ما تم تنفيذه

### 1. ASP.NET Core Admin API

- Onboarding لإنشاء الشركة وأول Owner.
- Login باستخدام JWT.
- إدارة Owner وPolicyAdmin وAuditor.
- إدارة الموظفين والأقسام وهوية Windows.
- تسجيل الأجهزة باستخدام Enrollment Code يستخدم مرة واحدة.
- ربط الجهاز بموظف، وإلغاء الجهاز والتوكن.
- إنشاء وتعديل وإلغاء Allow/Block.
- صلاحيات دائمة ومؤقتة وEmergency Deny.
- Scopes: Global، Employee، Device، Department، SID، Username، MachineName.
- إدارة Base Policy مع Sanitization وValidation.
- Audit للأحداث القادمة من الأجهزة ولتعديلات الأدمن.
- Heartbeat يحدد أن الجهاز يحتاج Refresh.
- Policy Compiler ينشئ Snapshot خاصة بالجهاز فقط.
- ECDSA P-256/SHA-256 في Production.
- Wrap/Unwrap لمفاتيح الملفات مع إعادة فحص صلاحية Encrypt/Decrypt مركزيًا.

### 2. SQL Server وEF Core

تمت إضافة Initial Migration تشمل Tenants وPolicies وAdminUsers وEmployees وDevices وEnrollmentCodes وPermissionGrants وSecurityEvents وAdminAuditLogs، مع Foreign Keys وUnique Indexes وعلاقات Tenant Isolation.

### 3. Angular Admin Portal

تمت إضافة واجهة Angular 21 تشمل:

- Onboarding وLogin.
- Dashboard.
- Administrators.
- Employees.
- Devices وEnrollment Codes.
- Permissions.
- Base Policy.
- Endpoint Audit وAdmin Audit.

الواجهة تستخدم Route Guards حسب الدور وJWT Interceptor، وتخزن جلسة التطوير في `sessionStorage` بدل Local Storage دائم.

### 4. ربط Windows Agent

- الـHeartbeat يقرأ `PolicyRefreshRequired` ويوقظ Policy Sync فورًا.
- يبقى Polling الدوري كخطة بديلة.
- Launcher التطوير يشغل Mock Server فقط عند استخدام DevelopmentNone المحلي.
- عند استخدام Admin API يعمل Health Check للباك ولا يشغل Mock Server.
- تمت إضافة Bootstrap Policy مركزية وسكربت ربط/Enrollment.

## ضوابط الأمان المضافة

- Device Tokens وEnrollment Codes لا تُخزن كنص واضح.
- PBKDF2-SHA256 لكلمات مرور الأدمن مع Salt و210,000 Iterations.
- إبطال JWT القديم فور تغيير الدور أو الحالة أو كلمة المرور باستخدام TokenVersion.
- منع تعطيل/تخفيض آخر Owner.
- Rate Limiting للـLogin/Onboarding وEnrollment.
- Policy Sanitizer وحدود أحجام وقواعد Regex آمنة.
- Policy خاصة بكل جهاز، دون تسريب Grants لموظفين آخرين.
- التحقق من Identity وSchema وDecision وTimestamp وPayload وIntegrity Hash في Audit.
- Idempotency للأحداث باستخدام `(TenantId, EventId)`.
- Audit Log لكل تعديل إداري.
- Fail-closed مع آخر Cache محمية صالحة على الجهاز.

## طريقة الفحص على Windows

```powershell
.\VERIFY_CENTRAL_ADMIN.bat
.\VERIFY_WINDOWS_READY.bat
```

ثم:

```powershell
.\START_CENTRAL_ADMIN.bat
```

وبعد إنشاء Tenant وEnrollment Code:

```powershell
.\CONNECT_DEVELOPMENT_TO_ADMIN.bat -TenantId '<TenantId>'
.\START_DEVELOPMENT.bat
```

## نتائج الفحص داخل بيئة إعداد الحزمة

- تم تنفيذ Angular Production Build بنجاح.
- تم التحقق من JSON وXML/YAML ومسارات المشاريع وبنية الملفات وفحوص C# النصية.
- لم يتوفر .NET SDK داخل بيئة إعداد الحزمة، كما تعذر تنزيله بسبب DNS، لذلك لم يتم الادعاء بأن `dotnet build` أو `dotnet test` تم تشغيلهما هنا.
- تم تضمين `VERIFY_CENTRAL_ADMIN.bat` ليشغل Restore وBuild وTests وAngular Clean Build على جهاز Windows الذي يحتوي المتطلبات.

## متطلبات قبل Production

- تشغيل جميع فحوص .NET واختبارات Windows الفعلية.
- HTTPS وSecret Manager وSQL Server Production.
- حماية مفتاح ECDSA الخاص وعدم وضعه في Source Control.
- KMS/HSM لمفاتيح الملفات في النشر الحقيقي.
- Authenticode وWDAC/Device Control وForce-installed browser extensions.
- نشر Angular من Web Tier مع Reverse Proxy للـAPI.
- إدارة Migrations من Pipeline بدل AutoMigrate.
- Backups وMonitoring وAlerting وRetention للـAudit.

التفاصيل الإضافية موجودة في `docs/CENTRAL_ADMIN_API.md` و`docs/PRODUCTION_GATES.md`.
