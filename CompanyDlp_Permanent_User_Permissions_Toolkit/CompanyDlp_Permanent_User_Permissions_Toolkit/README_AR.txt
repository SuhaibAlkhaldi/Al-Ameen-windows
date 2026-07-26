Company DLP Permanent User Permissions Toolkit
================================================

هذه الأدوات للاختبار المحلي فقط، بدون Backend وبدون Frontend وبدون dotnet build.

الفكرة:
- الصلاحية مربوطة بـWindows User SID.
- source = PermanentPolicy.
- expiresAtUtc = null.
- تستمر حتى يقوم الأدمن بإلغائها.
- الإلغاء لا يحذف السجل؛ يضع revokedAtUtc وrevokedBy.

1) إعطاء صلاحية دائمة:

powershell -ExecutionPolicy Bypass `
  -File .\SET_CompanyDlp_PERMANENT_USER_PERMISSION.ps1 `
  -ActionKey "screen.capture" `
  -WindowsUser "Suhaib" `
  -Decision Allow `
  -Reason "Permanent screenshot permission for Suhaib" `
  -ProjectRoot "."

2) عرض الصلاحيات النشطة:

powershell -ExecutionPolicy Bypass `
  -File .\LIST_CompanyDlp_PERMANENT_USER_PERMISSIONS.ps1 `
  -ProjectRoot "."

3) إلغاء الصلاحية باستخدام Grant ID:

powershell -ExecutionPolicy Bypass `
  -File .\REVOKE_CompanyDlp_PERMANENT_USER_PERMISSION.ps1 `
  -GrantId "ضع-هنا-Grant-ID" `
  -ProjectRoot "."

أو الإلغاء حسب المستخدم والأكشن:

powershell -ExecutionPolicy Bypass `
  -File .\REVOKE_CompanyDlp_PERMANENT_USER_PERMISSION.ps1 `
  -ActionKey "screen.capture" `
  -WindowsUser "Suhaib" `
  -ProjectRoot "."

ملاحظات:
- انتظر 10-15 ثانية بعد الإضافة أو الإلغاء.
- لا تحتاج إعادة تشغيل Service.
- كل تعديل يأخذ نسخة احتياطية من policy.development.json.
- لا يتم تعديل DLL أو EXE.
