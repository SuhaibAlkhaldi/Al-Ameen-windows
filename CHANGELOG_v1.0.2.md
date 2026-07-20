# Company DLP Windows Ready v1.0.2

## Fixed

- Added the direct `Microsoft.Extensions.Http` `8.0.1` dependency required by `IHttpClientFactory` and `AddHttpClient` in `CompanyDlp.Service`.
- Removed the WPF/Windows Forms `MessageBox` ambiguity by explicitly using `System.Windows.MessageBox` in the shell encryption/decryption command runner.
- Added the missing `System.IO` namespace to the screen-capture hotkey blocker for `Path.GetFileName`.
- Kept `System.Diagnostics.EventLog` aligned at `8.0.1` to avoid `NU1605` package downgrade failures.

## Validation command

```powershell
.\VERIFY_WINDOWS_READY.bat
```
