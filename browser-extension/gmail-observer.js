// Phase 7: Gmail send observer. Runs in the isolated content-script world on mail.google.com only.
//
// How it works:
//  - When an attachment is chosen (file input change, or a drop into the compose window), the file bytes are read in the
//    page and hashed with SHA-256 right away. This does not depend on how Gmail uploads the file, so it works for every
//    upload path. Files above FULL_HASH_LIMIT_BYTES are not hashed; they are sent without a hash and matched by name and size.
//  - When the Send button is clicked, the To/Cc recipients are read from the compose window and one observation is sent per
//    (attachment, recipient). Bcc is never read. If no recipient can be read, nothing is sent.
//  - Observations go to the agent through the extension's native channel. The agent queues them and sends them to the backend,
//    which decides the evidence levels and whether the file is sensitive.
//
// Nothing here is shown to the user, and no address or file name is written to the console.
(() => {
  "use strict";

  const CHANNEL = "GMAIL";
  const EXTENSION_VERSION = "3.0.12";
  const FULL_HASH_LIMIT_BYTES = 100 * 1024 * 1024;
  const POLICY_REFRESH_MS = 60 * 1000;
  // Gmail labels its send control with the UI language. English and Arabic are the two this product's users run.
  const SEND_LABEL_PREFIXES = ["Send", "إرسال"];

  let policyEnabled = false;
  let policyCheckedAtMs = 0;

  // Attachments chosen in the current compose window, keyed by name, size and last-modified time.
  const chosenAttachments = new Map();

  async function refreshPolicy() {
    if (Date.now() - policyCheckedAtMs < POLICY_REFRESH_MS) return;
    policyCheckedAtMs = Date.now();
    try {
      const context = await chrome.runtime.sendMessage({ type: "getContext" });
      policyEnabled = context?.policy?.fileTransferObservation?.enabled === true;
    } catch {
      policyEnabled = false;
    }
  }

  // The compose window is a dialog that contains a To field. Anything outside it is ignored.
  function composeRootOf(node) {
    const root = node?.closest?.('[role="dialog"]');
    return root && root.querySelector('[name="to"]') ? root : null;
  }

  function onFilesChosen(files) {
    console.info("[Al-Ameen] files chosen in compose:", files.length);
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
        entry.hash = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
      } catch {
        entry.hash = null;
      }
    }
    entry.ready = true;
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

    return [...seen].map(([email, role], index) => ({ email, role, ordinal: index }));
  }

  function roleOf(element) {
    const field = element.closest('[name="cc"], [name="bcc"], [name="to"]');
    const name = field?.getAttribute("name");
    if (name === "cc") return "CC";
    if (name === "bcc") return "BCC";
    return "TO";
  }

  // The attachment chips in the compose window, by file name. Returns null when the chips cannot be read, in which case
  // every chosen attachment is used.
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

  async function onSendClicked(button) {
    await refreshPolicy();
    console.info("[Al-Ameen] send clicked; observation enabled:", policyEnabled);
    if (!policyEnabled) return;

    const root = composeRootOf(button);
    if (!root) return;

    const recipients = readRecipients(root);
    console.info("[Al-Ameen] recipients read:", recipients.length);
    if (recipients.length === 0) {
      console.warn("[Al-Ameen] Gmail send: no readable recipients, nothing recorded.");
      return;
    }

    const present = attachmentNamesInWindow(root);
    const attachments = [...chosenAttachments.values()]
      .filter(attachment => attachment.ready && (present === null || present.has(attachment.name)));
    console.info("[Al-Ameen] attachments chosen:", chosenAttachments.size, "ready:", attachments.length);

    // Each attachment gets its own SendActionId, so the backend's (SendActionId, RecipientOrdinal) key stays unique when one
    // send carries several files. The recipients of one attachment share that id and differ only by RecipientOrdinal.
    for (const attachment of attachments) {
      const sendActionId = crypto.randomUUID();
      for (const recipient of recipients) {
        chrome.runtime.sendMessage({
          type: "fileTransferObserved",
          sendActionId,
          recipientOrdinal: recipient.ordinal,
          channel: CHANNEL,
          recipientRole: recipient.role,
          recipientKind: "EMAIL",
          recipientValue: recipient.email,
          recipientEvidenceType: "EMAIL_IN_SEND_FORM",
          fileName: attachment.name,
          fileSizeBytes: attachment.size,
          fileHashBytes: attachment.hash,
          contentFingerprint: null,
          observedAtUtc: new Date().toISOString(),
          extensionVersion: EXTENSION_VERSION
        }).catch(() => {});
      }
    }

    chosenAttachments.clear();
  }

  document.addEventListener("change", event => {
    const target = event.target;
    if (target instanceof HTMLInputElement && target.type === "file" && target.files?.length && composeRootOf(target)) {
      onFilesChosen(target.files);
    }
  }, true);

  document.addEventListener("drop", event => {
    if (!composeRootOf(event.target)) return;
    const files = event.dataTransfer?.files;
    if (files?.length) onFilesChosen(files);
  }, true);

  document.addEventListener("click", event => {
    const button = event.target?.closest?.('[role="button"], button');
    if (button && isSendButton(button)) {
      void onSendClicked(button);
    }
  }, true);
})();
