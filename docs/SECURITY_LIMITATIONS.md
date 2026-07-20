# Security limitations

This repository is a functional Windows DLP MVP and an engineering foundation, not a certification claim.

- A normal Windows application cannot stop a phone camera, external HDMI capture device, another operating system, or every kernel/administrator capture technique. The watermark is the attribution layer for those cases.
- `SetWindowDisplayAffinity` protects only windows owned by the calling application; it cannot mark arbitrary Chrome, Edge, Word, or PDF windows.
- The desktop keyboard hook blocks common screenshot shortcuts, but privileged software may capture through other APIs.
- Browser extension enforcement applies only to managed Chrome/Edge pages where the content script can run. Browser internal pages, extension pages, some protected viewers, and separate desktop applications are outside extension access. V10 blocks broad binary web transfer paths, but a signed driver, proxy, or application allowlisting is required for arbitrary desktop clients.
- Browser download blocking and Incognito/InPrivate control use enterprise browser policies.
- Any USB device that emulates only a keyboard or mouse is permitted because the business requirement explicitly allows any keyboard/mouse. Stronger BadUSB protection requires approved-device allowlisting or dedicated hardware controls.
- USB baseline trust is necessary to avoid disabling integrated webcams/Bluetooth readers that may appear as internal USB devices. The deployment image must be clean when the baseline is created.
- A user with local Administrator credentials, physical boot control, or the ability to reimage the device can ultimately remove endpoint software. Production deployment requires Standard User employee accounts, Secure Boot, disk encryption, controlled firmware boot, and central device management.
- Force-installing the production browser extension requires publishing it to the Chrome Web Store/Edge Add-ons or operating a valid enterprise self-hosted CRX update service. Loading an unpacked extension is intentionally only a development workflow.


## Important correction: screenshots

`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` protects only windows owned by the application that applies it. It cannot make every Chrome, Edge, Office, Notepad, or third-party window uncapturable. This project blocks common keyboard shortcuts and can terminate configured capture applications, but this is defense-in-depth rather than a guarantee. Stronger organization-wide protection requires application allowlisting (AppLocker/WDAC), managed browser policies, controlled viewers, or a VDI/remote desktop solution with screen-capture protection. A phone camera and privileged/kernel capture cannot be fully prevented.

## Email behavior

The clipboard classifier continues to block every complete syntactically valid email address, regardless of provider or domain. Manual typed/reconstructed email blocking inside browser fields is intentionally postponed and is disabled in the v10 policy (`blockSensitiveInputAndSubmit: false`). This avoids blocking ordinary email composition until the client approves the exact outbound-data rules.

## Encryption behavior

V10 manual encryption uses AES-256-GCM and a DPAPI-protected key tied to the current Windows user. This provides authenticated local encryption but is not an enterprise recovery-key system. Another Windows account, a reimaged PC, or a lost user profile may be unable to decrypt the files. Production deployment requires an IT-controlled key recovery/KMS design.
