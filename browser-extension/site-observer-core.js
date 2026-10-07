// Phase 7: shared send-observation logic for every site adapter (Gmail, Outlook, ...). A site adapter supplies only
// its own DOM knowledge (how to find the send button, the recipients, and - optionally - which chosen attachments
// are still present); everything else - hashing, the policy check, listening for file selection and the send click,
// and sending the observation to the agent - lives here once.
//
// How attachments are captured: whenever a file is chosen (file input change, or a drop), this reads the file's bytes
// and hashes them with SHA-256 immediately, in the page, before any upload happens. That makes it independent of how
// a given site actually uploads the file. Files above FULL_HASH_LIMIT_BYTES are not hashed; they are still reported,
// without a hash, and the backend matches them by name and size instead (a weaker match).
//
// Nothing here is shown to the user, and no address or file name is written to the console.
(() => {
  "use strict";

  const EXTENSION_VERSION = "3.0.12";
  const FULL_HASH_LIMIT_BYTES = 100 * 1024 * 1024;
  const POLICY_REFRESH_MS = 60 * 1000;

  let policyEnabled = false;
  let policyCheckedAtMs = 0;

  // Attachments chosen since the last send, keyed by name, size and last-modified time.
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
        entry.hash = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
      } catch {
        entry.hash = null;
      }
    }
    entry.ready = true;
  }

  async function onSendClicked(button, adapter) {
    // Everything DOM-related is read right here, synchronously, before any await below. Some sites (Outlook
    // observed live, 2026-10-07) tear down the compose window as soon as Send is clicked, optimistically, before
    // the message has actually gone out - by the time an awaited call resumes, the recipient fields are gone.
    // A site with no compose boundary of its own (adapter.composeRootOf absent) is read globally; one whose
    // boundary check fails for this click (e.g. a different "Send"-labelled button elsewhere) is skipped.
    const root = adapter.composeRootOf ? adapter.composeRootOf(button) : document;
    if (adapter.composeRootOf && !root) return;

    const recipients = adapter.readRecipients(root);

    // A site that can confirm which chosen attachments are still in the compose window (adapter.attachmentNamesInWindow)
    // drops any that were removed before sending. A site that cannot reports every attachment chosen since the last send.
    const present = adapter.attachmentNamesInWindow ? adapter.attachmentNamesInWindow(root) : null;
    const attachments = [...chosenAttachments.values()]
      .filter(attachment => attachment.ready && (present === null || present.has(attachment.name)));

    await refreshPolicy();
    if (!policyEnabled) return;

    if (recipients.length === 0) {
      console.warn(`[Al-Ameen] ${adapter.channel} send: no readable recipients, nothing recorded.`);
      return;
    }

    // Each attachment gets its own SendActionId, so the backend's (SendActionId, RecipientOrdinal) key stays unique
    // when one send carries several files. The recipients of one attachment share that id and differ only by ordinal.
    for (const attachment of attachments) {
      const sendActionId = crypto.randomUUID();
      for (const recipient of recipients) {
        chrome.runtime.sendMessage({
          type: "fileTransferObserved",
          sendActionId,
          recipientOrdinal: recipient.ordinal,
          channel: adapter.channel,
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

  function register(adapter) {
    document.addEventListener("change", event => {
      const target = event.target;
      if (target instanceof HTMLInputElement && target.type === "file" && target.files?.length
        && (!adapter.isWithinCompose || adapter.isWithinCompose(target))) {
        onFilesChosen(target.files);
      }
    }, true);

    document.addEventListener("drop", event => {
      if (adapter.isWithinCompose && !adapter.isWithinCompose(event.target)) return;
      const files = event.dataTransfer?.files;
      if (files?.length) onFilesChosen(files);
    }, true);

    document.addEventListener("click", event => {
      const button = event.target?.closest?.('[role="button"], button');
      if (button && adapter.isSendButton(button)) {
        void onSendClicked(button, adapter);
      }
    }, true);
  }

  window.__dlpFileTransferObserverCore = { register };
})();
