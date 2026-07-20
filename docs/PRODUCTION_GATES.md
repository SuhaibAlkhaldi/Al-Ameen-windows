# Production Release Gates

Do not deploy to employees until all gates are complete.

## Build and signing

- Reproducible Release build passes on the supported Windows build image.
- Unit/integration tests pass.
- Every EXE, DLL, MSI/MSIX, script catalog, and driver is signed by the approved certificate.
- Timestamping and certificate-rotation procedures are documented.
- Installer verifies signatures and refuses unsigned binaries.

## Windows control plane

- WDAC/App Control base and supplemental policies are designed, audited, piloted, and rollback-tested.
- Browser executables/extensions/native host are allowed; unapproved portable apps and installers are denied.
- Device Installation Restrictions or Defender Device Control policy is deployed and rollback-tested.
- Service recovery, ACLs, uninstall authorization, and break-glass procedures are approved.

## Backend

- HTTPS with trusted certificate; optional mTLS/device certificate where required.
- Enrollment, token rotation/revocation, tenant isolation, and rate limiting.
- ECDSA policy signing and protected private key.
- Idempotent audit ingestion and append-only retention.
- KMS/HSM envelope key service and authorization.
- Monitoring for offline agents, queue growth, policy rejection, tampering, and clock rollback.

## Browser

- Chrome and Edge extensions are signed/published or enterprise self-hosted.
- Correct IDs/update URLs are placed in production policy.
- Force-install and block-removal policies are verified.
- Incognito/guest/unmanaged browsers are controlled.

## Acceptance

- Complete `WINDOWS_TEST_PLAN.md` on clean Windows VMs and representative physical devices.
- Security review verifies no sensitive plaintext in logs/outbox/database payloads.
- Penetration and tamper testing completed.
- Upgrade, rollback, uninstall, disaster recovery, and certificate expiry tested.
