using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Phase 8: desktop sibling of Phase 7's browser-extension recipient detection, for WhatsApp Desktop specifically.
// See WebViewDesktopObserver (the shared base class) for the CDP connect/inject/listen machinery and
// WebView2DebugPort for the dynamic-port discovery; this class only supplies WhatsApp Desktop's own identity and
// injected script.
//
// Confirmed live, end to end, 2026-10-13:
//   - WhatsApp Desktop's WebView2 renders the real https://web.whatsapp.com page - not a native recreation of it.
//     Windows UI Automation was tried first and found to expose nothing for it (FindAll against the top-level
//     window found only 4 generic Pane elements; scanning the WebView2 renderer process and all 7 of its own
//     child processes found 0 UIA elements anywhere).
//   - Unlike Microsoft Teams, WhatsApp Desktop only ever has one matching CDP page target at a time in testing -
//     WebViewDesktopObserver's multi-target support is harmless overhead for it, not a requirement.
//   - A page-side call to window.dlpNotify(json) arrives back over the session's WebSocket as a
//     Runtime.bindingCalled event, whose params.payload is that json string.
//   - Killing and relaunching WhatsApp Desktop mid-session is recoverable: polling found the new page target
//     within ~2 seconds in testing.
//
// InjectedScript is a deliberately standalone sibling of browser-extension/site-observer-core.js +
// browser-extension/adapters/whatsapp.js, not a shared file with the browser extension: there is no
// chrome.runtime inside WhatsApp Desktop's WebView2 (no extension is installed there), so the one thing that
// cannot be reused verbatim is the transport (chrome.runtime.sendMessage -> window.dlpNotify) and the policy
// gate (site-observer-core.js checks chrome.runtime for it; here, the policy is already checked by the base
// class before this script is ever injected, so the page-side script does not need to check it again). Every
// selector (readRecipients, isSendButton, isSendKeydown) and the attachment-hashing logic is copied unchanged
// from whatsapp.js/site-observer-core.js, since this is literally the same web.whatsapp.com DOM.
public sealed class WhatsAppDesktopObserver(
    PolicyStore policyStore,
    FileTransferOutbox fileTransferOutbox,
    ILogger<WhatsAppDesktopObserver> logger)
    : WebViewDesktopObserver(policyStore, fileTransferOutbox, logger)
{
    protected override string ProcessName => "WhatsApp.Root";
    protected override string PackageFamilyName => "5319275A.WhatsAppDesktop_cv1g1gvanyjgm";
    protected override string Channel => "WHATSAPP_DESKTOP";

    protected override bool IsPolicyEnabled(DlpPolicy policy) => policy.WhatsAppDesktopObservation.Enabled;

    protected override bool MatchesTarget(string url) =>
        url.StartsWith("https://web.whatsapp.com", StringComparison.OrdinalIgnoreCase);

    // window.__dlpWhatsAppDesktopInjected guards against double-registration: this is injected twice per session
    // (once as Page.addScriptToEvaluateOnNewDocument, for any future reload, and once via an immediate
    // Runtime.evaluate, for the page as it already is right now), and again on every reconnect of this observer
    // even if WhatsApp Desktop's own page never reloaded.
    protected override string InjectedScript => """
        (() => {
          "use strict";
          if (window.__dlpWhatsAppDesktopInjected) return;
          window.__dlpWhatsAppDesktopInjected = true;

          const CHANNEL = "WHATSAPP_DESKTOP";
          const FULL_HASH_LIMIT_BYTES = 100 * 1024 * 1024;
          const chosenAttachments = new Map();

          function isGroupChat() {
            return document.querySelector('[data-testid="chat-subtitle"]') !== null;
          }

          function readRecipients() {
            const titleEl = document.querySelector(
              '[data-testid="conversation-info-header"] [data-testid="conversation-info-header-chat-title"]'
            );
            const name = (titleEl?.textContent || "").trim();
            if (!name) return [];
            return isGroupChat()
              ? [{ value: name, role: null, ordinal: 0, kind: "GROUP_NAME", evidenceType: "GROUP_NAME" }]
              : [{ value: name, role: null, ordinal: 0, kind: "CHAT_NAME", evidenceType: "CHAT_CONTACT_NAME" }];
          }

          function isSendButton(element) {
            return element.querySelector('[data-testid="wds-ic-send-filled"]') !== null;
          }

          function isSendKeydown(event) {
            if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return false;
            if (event.isComposing) return false;
            return !!event.target?.closest?.('[contenteditable="true"][role="textbox"]');
          }

          function onFilesChosen(files) {
            for (const file of files) {
              if (!(file instanceof File)) continue;
              void hashAndRemember(file);
            }
          }

          async function hashAndRemember(file) {
            const key = `${file.name}|${file.size}|${file.lastModified}`;
            if (chosenAttachments.has(key)) return;
            const entry = { name: file.name, size: file.size, hash: null, ready: false };
            chosenAttachments.set(key, entry);
            if (file.size <= FULL_HASH_LIMIT_BYTES) {
              try {
                const digest = await crypto.subtle.digest("SHA-256", await file.arrayBuffer());
                entry.hash = Array.from(new Uint8Array(digest), b => b.toString(16).padStart(2, "0")).join("");
              } catch {
                entry.hash = null;
              }
            }
            entry.ready = true;
          }

          async function onSendTriggered() {
            const recipients = readRecipients();
            if (recipients.length === 0) return;
            const attachments = [...chosenAttachments.values()].filter(a => a.ready);

            for (const attachment of attachments) {
              const sendActionId = crypto.randomUUID();
              for (const recipient of recipients) {
                try {
                  window.dlpNotify(JSON.stringify({
                    sendActionId,
                    recipientOrdinal: recipient.ordinal,
                    channel: CHANNEL,
                    recipientRole: recipient.role ?? null,
                    recipientKind: recipient.kind,
                    recipientValue: recipient.value,
                    recipientEvidenceType: recipient.evidenceType,
                    fileName: attachment.name,
                    fileSizeBytes: attachment.size,
                    fileHashBytes: attachment.hash,
                    observedAtUtc: new Date().toISOString()
                  }));
                } catch { /* binding not available - should not happen once injected */ }
              }
            }
            chosenAttachments.clear();
          }

          document.addEventListener("change", event => {
            const target = event.target;
            if (target instanceof HTMLInputElement && target.type === "file" && target.files?.length) {
              onFilesChosen(target.files);
            }
          }, true);

          document.addEventListener("drop", event => {
            const files = event.dataTransfer?.files;
            if (files?.length) onFilesChosen(files);
          }, true);

          document.addEventListener("click", event => {
            const button = event.target?.closest?.('[role="button"], button');
            if (button && isSendButton(button)) {
              void onSendTriggered();
            }
          }, true);

          document.addEventListener("keydown", event => {
            if (isSendKeydown(event)) {
              void onSendTriggered();
            }
          }, true);
        })();
        """;
}
