# Company DLP Windows Ready v1.0.1

## Fixed

- Aligned `System.Diagnostics.EventLog` with `Microsoft.Extensions.Hosting.WindowsServices` at version `8.0.1`.
- Resolves NuGet restore error `NU1605` caused by the direct `8.0.0` reference downgrading the transitive `8.0.1` dependency.

## Validation command

```powershell
.\VERIFY_WINDOWS_READY.bat
```
