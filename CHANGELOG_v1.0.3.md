# Company DLP Windows Ready v1.0.3

- Added non-executable `CompanyDlp.Core` for policy evaluation, classification, policy storage, DPAPI protection, browser notification rules, and the file-protection engine.
- Tests reference Core and Contracts only; they no longer load Service/Desktop executable assemblies inside testhost.
- Removed the unused legacy Desktop encryption implementation. The Service-side `FileProtectionEngine` remains the only Encrypt/Decrypt implementation.
- Added `ITrustedClock` and `IFileKeyProtector` ports.
- Validation removes Mark-of-the-Web from the extracted developer source tree before compilation; Production signing gates are unchanged.
