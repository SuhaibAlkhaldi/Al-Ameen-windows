// Phase 7: Gmail's adapter for site-observer-core.js. Runs in the isolated content-script world on mail.google.com
// only, after site-observer-core.js (declared first for this match in manifest.json).
(() => {
  "use strict";

  // Gmail labels its send control with the UI language. English and Arabic are the two this product's users run.
  const SEND_LABEL_PREFIXES = ["Send", "إرسال"];

  // The compose window is a dialog that contains a To field. Anything outside it is ignored.
  function composeRootOf(node) {
    const root = node?.closest?.('[role="dialog"]');
    return root && root.querySelector('[name="to"]') ? root : null;
  }

  // Reads To and Cc recipients from the compose window. Bcc is skipped on purpose. Ordinals are assigned after filtering.
  function readRecipients(root) {
    const seen = new Map();
    root.querySelectorAll("[email]").forEach(element => {
      const email = (element.getAttribute("email") || "").trim().toLowerCase();
      if (!email.includes("@")) return;

      const role = roleOf(element);
      if (role === "BCC") return;
      if (!seen.has(email)) seen.set(email, role);
    });

    return [...seen].map(([email, role], index) => ({ value: email, role, ordinal: index }));
  }

  function roleOf(element) {
    const field = element.closest('[name="cc"], [name="bcc"], [name="to"]');
    const name = field?.getAttribute("name");
    if (name === "cc") return "CC";
    if (name === "bcc") return "BCC";
    return "TO";
  }

  // The attachment chips in the compose window, by file name. Returns null when the chips cannot be read, in which
  // case every chosen attachment is used.
  function attachmentNamesInWindow(root) {
    const names = new Set();
    root.querySelectorAll("[download_url]").forEach(element => {
      const parts = (element.getAttribute("download_url") || "").split(":");
      if (parts.length >= 3) names.add(parts[1]);
    });
    return names.size > 0 ? names : null;
  }

  function isSendButton(element) {
    const label = (element.getAttribute("data-tooltip") || element.getAttribute("aria-label") || "").trim();
    return SEND_LABEL_PREFIXES.some(prefix => label.startsWith(prefix)) && composeRootOf(element) !== null;
  }

  function isWithinCompose(node) {
    return composeRootOf(node) !== null;
  }

  window.__dlpFileTransferObserverCore.register({
    channel: "GMAIL",
    recipientKind: "EMAIL",
    recipientEvidenceType: "EMAIL_IN_SEND_FORM",
    composeRootOf,
    readRecipients,
    attachmentNamesInWindow,
    isSendButton,
    isWithinCompose
  });
})();
