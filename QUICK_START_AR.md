# البدء السريع — Company DLP v1.1.0

## المتطلبات

- Windows 10 أو 11 بنواة x64.
- .NET 8 SDK.
- SQL Server LocalDB للتطوير، أو SQL Server آخر.
- Node.js 20 أو أحدث مع npm.
- PowerShell، ويفضل فتحه كمسؤول عند اختبار خصائص Windows.

## 1. فحص الباك والواجهة والـAgent

من داخل مجلد المشروع:

```powershell
.\VERIFY_CENTRAL_ADMIN.bat
.\VERIFY_WINDOWS_READY.bat
```

الأول يبني حل .NET ويشغل الاختبارات ويبني Angular Production Build. الثاني يشغل فحوص نسخة Windows الحالية.

## 2. تشغيل لوحة الأدمن والـAPI

```powershell
.\START_CENTRAL_ADMIN.bat
```

سيتم فتح نافذتين:

- API: `http://127.0.0.1:5060`
- Portal: `http://127.0.0.1:4200`

افتح الـPortal ثم اختر **Create tenant** لإنشاء الشركة وأول حساب بدور `Owner`.

بعد الدخول يمكنك:

1. إنشاء حسابات Owner أو PolicyAdmin أو Auditor.
2. إضافة الموظفين والأقسام وبيانات Windows الخاصة بهم.
3. إنشاء Enrollment Code يستخدم مرة واحدة.
4. ربط الجهاز بموظف.
5. إضافة Allow أو Block دائم أو مؤقت.
6. مراجعة أحداث الأجهزة وسجل تعديلات الأدمن.

## 3. ربط نسخة Windows بالباك المركزي

من صفحة Devices أنشئ Enrollment Code، ثم شغل:

```powershell
.\CONNECT_DEVELOPMENT_TO_ADMIN.bat -TenantId '<TenantId>'
```

ألصق الكود عندما يطلبه السكربت، ثم شغل:

```powershell
.\START_DEVELOPMENT.bat
```

عندما تكون نسخة التطوير متصلة بالـAdmin API، لن يشغل السكربت الـMock Server. سيعمل Health Check للـAPI ثم يشغل Service وDesktop.

## 4. تجربة صلاحية مؤقتة

من صفحة **Permissions**:

- اختر `screen.capture`.
- اختر `Allow`.
- اختر Scope من نوع `Device` وحدد الجهاز.
- فعّل الصلاحية المؤقتة وحدد وقت الانتهاء.
- اكتب سبب الموافقة واحفظ.

يرفع الباك رقم `PolicyRevision`. في أول Heartbeat يعرف الـAgent أن هناك تعديلًا، فيسحب Policy خاصة بجهازه ويطبقها. عند انتهاء الوقت تتوقف الصلاحية محليًا دون انتظار أمر جديد من الباك.

## 5. الأدوار

- `Owner`: كامل الصلاحيات، ويستطيع إدارة حسابات الأدمن.
- `PolicyAdmin`: يدير الموظفين والأجهزة والصلاحيات والـPolicy ويقرأ الـAudit.
- `Auditor`: قراءة الـAudit فقط.

لا يمكن تعطيل أو تخفيض آخر Owner. تغيير الدور أو الحالة أو كلمة المرور يبطل الجلسات القديمة مباشرة.

## 6. الإيقاف والتنظيف

أغلق شاشة Company DLP لإيقاف بيئة تطوير الـAgent وتنظيف تغييرات المتصفح والـRegistry المؤقتة. عند حدوث إغلاق غير طبيعي شغل:

```powershell
.\RESTORE_MY_PC.bat
```

## قبل Production

لا تستخدم مفاتيح أو إعدادات Development. طبّق `docs\PRODUCTION_GATES.md`، واستخدم HTTPS وSQL Server Production ومفتاح ECDSA محميًا وSecret Manager وCode Signing، ثم نفذ `docs\WINDOWS_TEST_PLAN.md` على أجهزة Windows فعلية.
