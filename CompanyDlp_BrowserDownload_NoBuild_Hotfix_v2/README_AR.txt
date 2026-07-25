Company DLP - browser.download Temporary Permission (No-Build Hotfix)
====================================================================

هذا الإصلاح لا يغيّر أي C# أو DLL أو EXE أو Windows Service ولا يشغّل dotnet build.

ما الذي يفعله؟
- يضيف browser.download كصلاحية Default Deny داخل policy.development.json.
- يجعل Browser Extension تقيم Temporary/Permanent/Emergency grants مباشرة.
- يزيل HKCU DownloadRestrictions الذي كان يمنع التنزيل قبل تقييم الصلاحية.
- يحدث Chrome/Edge وFirefox extensions إلى 3.0.1.
- يحدث set-development-permission.ps1 ويصلح إرسال تواريخ ISO-8601.

طريقة التطبيق:
powershell -ExecutionPolicy Bypass -File .\APPLY_CompanyDlp_BROWSER_DOWNLOAD_NO_BUILD_HOTFIX.ps1 -ProjectRoot .

بعد التطبيق:
1) أغلق نافذة متصفح الاختبار بالكامل.
2) لا تغلق START_DEVELOPMENT.bat.
3) اضغط Start Test Session من جديد.
4) انتظر 10-15 ثانية.
5) نفّذ:

powershell -ExecutionPolicy Bypass `
  -File .\set-development-permission.fixed.ps1 `
  -ActionKey "browser.download" `
  -Minutes 2 `
  -Decision Allow `
  -Reason "Temporary download test"

أثناء الدقيقتين يجب أن يعمل التنزيل. بعد الانتهاء يرجع الحظر تلقائياً.
