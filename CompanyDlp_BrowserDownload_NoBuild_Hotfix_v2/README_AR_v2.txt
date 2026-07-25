Company DLP browser.download Hotfix v2
=============================================

سبب v2:
الإصدار السابق كان ينتظر قرار الصلاحية قبل إلغاء التنزيل. الملفات الصغيرة جدًا
قد تنتهي قبل وصول القرار، لذلك كانت تنزل بدون Alert.

v2:
- يوقف التنزيل فور إنشائه.
- يقرأ browser.download من الـPolicy.
- عند Allow يستأنف التنزيل.
- عند Block يلغي التنزيل ويحذف الملف حتى لو اكتمل بسرعة.
- يعرض Alert داخل الصفحة، أو Windows notification كبديل.
- لا يعدل C# أو DLL أو EXE ولا يشغل dotnet build.

التطبيق:
powershell -ExecutionPolicy Bypass `
  -File .\CompanyDlp_BrowserDownload_NoBuild_Hotfix_v2\APPLY_CompanyDlp_BROWSER_DOWNLOAD_NO_BUILD_HOTFIX_v2.ps1 `
  -ProjectRoot .

بعد التطبيق:
1. أغلق متصفح الاختبار المحمي فقط.
2. اضغط Start Test Session.
3. افتح chrome://extensions أو edge://extensions وتأكد أن النسخة 3.0.2.
4. جرّب التنزيل بدون صلاحية: يجب أن يُحذف ويظهر Alert.
5. أعط browser.download Allow مؤقتًا وجرّب مجددًا.
