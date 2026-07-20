# البدء السريع — Company DLP Windows Ready v1

## قبل التشغيل

- استخدم Windows 10/11 x64.
- افتح PowerShell كمسؤول عند اختبار USB وسياسات الجهاز.
- يلزم .NET SDK 8 أو أحدث.

## 1. التحقق من النسخة

من داخل مجلد المشروع:

```powershell
.\VERIFY_WINDOWS_READY.bat
```

يجب أن ينجح Restore وBuild وTests وفحص ملفات JSON وPowerShell وJavaScript.

## 2. تشغيل بيئة الاختبار الكاملة

```powershell
.\START_DEVELOPMENT.bat
```

السكربت يقوم بما يلي:

1. يعيد أي تغييرات تطوير سابقة.
2. يبني الحل.
3. يشغّل Mock Server المتوافق مع عقد الباك النهائي.
4. يشغّل Service وDesktop.
5. يسجل Right-click Encrypt/Decrypt مؤقتًا.
6. ينظف التغييرات عند إغلاق شاشة Company DLP.

بعد فتح الشاشة اضغط **Start Test Session** لفتح Chrome/Edge بملف مستخدم مؤقت ومحمي. لا تختبر الرفع في نافذة متصفح عادية.

## 3. إعطاء صلاحية مؤقتة

اترك بيئة التطوير مفتوحة وافتح PowerShell آخر:

```powershell
.\SET_DEVELOPMENT_PERMISSION.bat
```

اختر الـAction والمدة. تنتهي الصلاحية تلقائيًا ويُسجل حدث انتهاء الصلاحية.

## 4. مشاهدة الأحداث

```powershell
.\SHOW_DEVELOPMENT_EVENTS.bat
```

الأحداث تمر بنفس Envelope وBatch Contract الذي سيستخدمه الباك الحقيقي. أثناء التطوير يخزن Mock Server نسخة JSONL للعرض؛ الـAgent نفسه يحتفظ بالـOutbox مشفرة بـDPAPI.

## 5. الإيقاف والتنظيف

أغلق شاشة Company DLP. للتنظيف اليدوي عند انقطاع مفاجئ:

```powershell
.\RESTORE_MY_PC.bat
```

## مهم قبل اعتماد النسخة

نجاح الاختبار النصي للحزمة لا يكفي. يجب تشغيل `VERIFY_WINDOWS_READY.bat` على Windows ثم تنفيذ المصفوفة الموجودة في:

```text
docs\WINDOWS_TEST_PLAN.md
```

النسخة الحالية تمنع كل Upload افتراضيًا. عقد AI Provider جاهز، لكن السماح بعد قرار AI يحتاج Approval Token قصير المدة مربوطًا بالملف والمستخدم والجهاز والموقع قبل تفعيله في Production.

## ملاحظات v1.0.10

- في وضع Development، لن يتم منع أو تسجيل أو إظهار تنبيه تثبيت البرامج قبل الضغط على **Start test session**.
- بعد الضغط على **Stop** تتوقف مراقبة تثبيت البرامج تلقائيًا.
- يتم تشغيل واجهة Desktop من `CompanyDlp.Desktop.exe` مباشرة لتجنب حظر تحميل الـDLL على أجهزة Application Control.
