[HISTORICAL - already applied, do not re-run]
This hotfix's ordering fix (show the alert immediately, audit in the background) is already live in
browser-extension/service-worker.js (Chrome/Edge, since evolved further to manifest 3.0.7) and, as of
the 2026 secondary-audit cleanup pass, has also been ported to firefox-extension/background-firefox.js
(bumped to manifest 3.0.3). This folder is kept only as a record of how the Chrome/Edge fix was
originally delivered - do not re-apply the .ps1 script here.

Company DLP Browser Download Alert Hotfix v3
=================================================

المشكلة:
التنزيل يُحظر ويُحذف، لكن Alert لا يظهر لأن الكود كان ينتظر إرسال Audit
إلى الـService قبل استدعاء التنبيه.

الإصلاح:
- يعرض التنبيه فور حذف التنزيل.
- يرسل Audit بالخلفية بعد ذلك.
- يرفع نسخة الـExtension إلى 3.0.3.
- لا يعدل C# أو DLL أو EXE ولا يشغل dotnet build.

التشغيل من جذر المشروع:

powershell -ExecutionPolicy Bypass `
  -File .\CompanyDlp_BrowserDownload_Alert_Hotfix_v3\APPLY_CompanyDlp_BROWSER_DOWNLOAD_ALERT_HOTFIX_v3.ps1 `
  -ProjectRoot .

بعدها:
1. افتح chrome://extensions أو edge://extensions.
2. اضغط Reload على Company DLP Browser Protection.
3. تأكد أن Version = 3.0.3.
4. جرّب التنزيل بدون Temporary Permission.
