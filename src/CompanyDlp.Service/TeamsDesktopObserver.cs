using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Phase 8: desktop sibling of Phase 7's browser-extension recipient detection, for the new Microsoft Teams
// specifically. See WebViewDesktopObserver (the shared base class) for the CDP connect/inject/listen machinery and
// WebView2DebugPort for the dynamic-port discovery; this class only supplies Teams Desktop's own identity and
// injected script.
//
// Confirmed live, end to end, 2026-10-13:
//   - The new Microsoft Teams desktop app is WebView2-hosted and renders the real teams.microsoft.com (work/school
//     tenant) and/or teams.live.com (personal tenant) pages - the exact same sites browser-extension/adapters/
//     teams.js already reads for Phase 7's TEAMS channel, not a native recreation of them.
//   - Unlike WhatsApp Desktop, this app can have several matching CDP page targets open at the same time: one per
//     signed-in tenant's main window, plus one per pop-out chat window the user has floated into its own window -
//     confirmed live with both a work-tenant window and a personal-tenant pop-out chat window open simultaneously.
//     WebViewDesktopObserver attaches to every matching target concurrently for exactly this reason.
//
// InjectedScript is a standalone sibling of browser-extension/site-observer-core.js + browser-extension/adapters/
// teams.js (see WhatsAppDesktopObserver's header comment for why these cannot be shared files verbatim with the
// browser extension - no chrome.runtime inside this WebView2). readRecipients and isSendButton are copied
// unchanged from teams.js; there is no isSendKeydown here either, matching teams.js (Teams has no Enter-to-send
// keyboard path the web adapter relies on - its Ctrl+Enter shortcut is not handled separately there, and this
// desktop observer makes the same choice for the same reason: unverified whether it even needs its own handling,
// and not worth a live-unconfirmed addition). The attachment-hashing listeners (change/drop) are the generic ones
// site-observer-core.js provides to every adapter, copied unchanged.
public sealed class TeamsDesktopObserver(
    PolicyStore policyStore,
    FileTransferOutbox fileTransferOutbox,
    ILogger<TeamsDesktopObserver> logger)
    : WebViewDesktopObserver(policyStore, fileTransferOutbox, logger)
{
    protected override string ProcessName => "ms-teams";
    protected override string PackageFamilyName => "MSTeams_8wekyb3d8bbwe";
    protected override string Channel => "TEAMS_DESKTOP";

    protected override bool IsPolicyEnabled(DlpPolicy policy) => policy.TeamsDesktopObservation.Enabled;

    protected override bool MatchesTarget(string url) =>
        url.StartsWith("https://teams.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://teams.live.com", StringComparison.OrdinalIgnoreCase);

    // window.__dlpTeamsDesktopInjected guards against double-registration - see WhatsAppDesktopObserver's
    // InjectedScript comment for why this fires more than once per session.
    protected override string InjectedScript => """
        (() => {
          "use strict";
          if (window.__dlpTeamsDesktopInjected) return;
          window.__dlpTeamsDesktopInjected = true;

          const CHANNEL = "TEAMS_DESKTOP";
          const SELF_SUFFIX = " (You)";
          const FULL_HASH_LIMIT_BYTES = 100 * 1024 * 1024;
          const chosenAttachments = new Map();

          function readRecipients() {
            const seen = new Map();
            document.querySelectorAll('[data-tid^="participant-"]').forEach(element => {
              const identity = element.getAttribute("data-tid");
              const name = (element.textContent || "").trim();
              if (!name || name.endsWith(SELF_SUFFIX)) return;
              if (!seen.has(identity)) seen.set(identity, name);
            });

            return [...seen.values()].map((name, index) => ({
              value: name,
              role: null,
              ordinal: index,
              kind: "CHAT_NAME",
              evidenceType: "TEAMS_CHAT_NAME"
            }));
          }

          const SEND_BUTTON_DATA_TIDS = new Set(["sendMessageCommands-send", "newMessageCommands-send"]);

          function isSendButton(element) {
            return SEND_BUTTON_DATA_TIDS.has(element.getAttribute("data-tid"));
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
        })();
        """;
}
