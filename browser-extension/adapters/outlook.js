// Phase 7: Outlook (web) adapter for site-observer-core.js. Runs on outlook.live.com and outlook.office.com, after
// site-observer-core.js (declared first for this match in manifest.json).
//
// Verified live on outlook.live.com (2026-10-07) by inspecting a real compose window:
//   - The To, Cc and Bcc fields each sit in their own element with role="group" and an id ending in
//     "_TO" / "_CC" / "_BCC" (e.g. "MSG_15127c3b5a0_TO"). The prefix is a per-compose random id; only the suffix
//     is stable, and it does not depend on the UI language.
//   - A recipient resolved from the address book renders as an entity chip (class "_EType_RECIPIENT_ENTITY")
//     whose own aria-label is the address itself - e.g. aria-label="name@example.com" - even when the chip's
//     VISIBLE text is a friendly display name with no "@" in it at all. A typed address the user has not yet
//     resolved to a contact has no chip; it is still plain text inside the field. Both are handled: chip
//     aria-labels are read first, and plain text is only used as a fallback when no chip matched.
//   - The send button's id always contains "primaryActionButton" (e.g. "splitButton-r3a__primaryActionButton"),
//     regardless of UI language.
//   - Bcc's own group (suffix "_BCC") was confirmed to exist, but it is never queried - this adapter does not
//     call findGroup("_BCC") anywhere, so a Bcc address can never reach readRecipients.
//
// Not yet verified: how to tell whether a chosen attachment is still present at send time (no selector found for
// Outlook's attachment list), so attachmentNamesInWindow is omitted and every hashed attachment since the last
// send is reported - see the Phase 7 plan's open points.
(() => {
  "use strict";

  const EMAIL_PATTERN = /[^\s,;]+@[^\s,;]+\.[^\s,;]+/g;

  function findGroup(idSuffix) {
    return [...document.querySelectorAll('[role="group"]')].find(element => element.id?.endsWith(idSuffix)) ?? null;
  }

  function extractEmails(container) {
    if (!container) return [];

    // Resolved contact chips first: their aria-label is the real address, regardless of what display name is shown.
    const chipAddresses = [...container.querySelectorAll("[aria-label]")]
      .map(element => (element.getAttribute("aria-label") || "").trim())
      .filter(label => label.includes("@"));
    if (chipAddresses.length > 0) {
      return chipAddresses.map(address => address.toLowerCase());
    }

    // Fallback: an address the user typed but has not yet resolved to a contact has no chip yet, only plain text.
    const matches = container.textContent?.match(EMAIL_PATTERN) ?? [];
    return matches.map(match => match.replace(/[.,;]+$/, "").toLowerCase());
  }

  // Reads To and Cc. Bcc's group is never looked up here, by design - see the file header.
  function readRecipients() {
    const seen = new Map();
    extractEmails(findGroup("_TO")).forEach(email => {
      if (!seen.has(email)) seen.set(email, "TO");
    });
    extractEmails(findGroup("_CC")).forEach(email => {
      if (!seen.has(email)) seen.set(email, "CC");
    });

    return [...seen].map(([email, role], index) => ({ value: email, role, ordinal: index }));
  }

  function isSendButton(element) {
    return typeof element.id === "string" && element.id.includes("primaryActionButton");
  }

  window.__dlpFileTransferObserverCore.register({
    channel: "OUTLOOK",
    recipientKind: "EMAIL",
    recipientEvidenceType: "EMAIL_IN_SEND_FORM",
    readRecipients,
    isSendButton
    // No composeRootOf: the _TO/_CC group ids already belong to one specific compose, so there is nothing further
    // to scope by. No attachmentNamesInWindow: see the file header.
  });
})();
