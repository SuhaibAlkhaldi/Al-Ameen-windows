// Phase 7: Yahoo Mail (web) adapter for site-observer-core.js. Runs on mail.yahoo.com only, after
// site-observer-core.js (declared first for this match in manifest.json).
//
// Verified live on mail.yahoo.com (2026-10-11), across three real sends, against the "Compact compose" layout
// (the default offered in the "Compact compose is now available" dialog - "Full-sized" mode was never
// exercised with a real send and may need its own selectors if its DOM differs):
//   - Several of Yahoo's own data-test-id values are NOT unique within one compose popup - confirmed live that
//     both "compose" and "compact-compose-item" are each stamped on two different elements in the same popup
//     (one wrapping the header/recipient fields, a separate one wrapping the toolbar), so closest() from the
//     send button can resolve to the toolbar's narrower copy, which has no recipient field as a descendant.
//     The real per-popup boundary both the send button's and the recipient fields' ancestor chains share is
//     data-test-id="compact-compose-container" (one level above the duplicated "compact-compose-item") - used
//     here as composeRootOf.
//   - The recipient header has an expanded state and a collapsed one, and BOTH "container-to"/"container-cc"
//     AND "compose-header-field-to"/"-cc" (two different wrapper schemes tried live) turned out to only exist
//     in the expanded state - confirmed live that Yahoo's compact compose collapses the header (e.g. once a
//     file is attached) into a single data-test-id="collapsed-recipient-pills-container" with no per-role
//     wrapper at all, and every real send observed reached the collapsed state by click time. So role is read
//     by positively matching Cc's own field wrapper only ("compose-header-field-cc") and defaulting everything
//     else to TO - correct in the (more common) expanded state, and the only thing crossable in the collapsed
//     one. Open gap, not fixed: if Bcc also lands in that same collapsed container indistinguishably from To
//     once collapsed, a Bcc recipient would be misrecorded as TO - not verified live (no Bcc value was ever
//     entered during testing), see the Phase 7 plan's open points.
//   - A resolved recipient renders as a pill: data-test-id="pill", whose aria-label (and title, wrapped in
//     "<...>") IS the email address itself, regardless of what display name the pill shows as text, in both the
//     expanded and collapsed states. No fallback for unresolved/typed-but-not-yet-chipped text was found
//     necessary - Yahoo chips a typed address as soon as it's accepted (Enter/comma), unlike Outlook which can
//     leave typed text un-chipped.
//   - The send button always carries data-test-id="compose-send-button", regardless of UI language.
//   - The attach-files button is data-test-id="icon-btn-attach", but its actual <input type="file"> is NOT a
//     descendant of compact-compose-container - confirmed live it lives elsewhere in the DOM (likely portal-
//     rendered), which is why isWithinCompose is omitted entirely (see below). No selector was found for a
//     per-compose attachment-chip list either, so attachmentNamesInWindow is also omitted and every hashed
//     attachment since the last send is reported, same open point already documented for Outlook and Teams -
//     see the Phase 7 plan's open points.
(() => {
  "use strict";

  function composeRootOf(node) {
    return node?.closest?.('[data-test-id="compact-compose-container"]') ?? null;
  }

  // Reads every recipient pill under the compose root, in whichever header state (expanded or collapsed - see
  // file header) is active at click time. Role is CC only when positively inside Cc's own field wrapper;
  // everything else defaults to TO, which is also the only thing left crossable once the header is collapsed.
  function readRecipients(root) {
    const seen = new Map();
    [...root.querySelectorAll('[data-test-id="pill"]')].forEach(pill => {
      const email = (pill.getAttribute("aria-label") || "").trim().toLowerCase();
      if (!email.includes("@") || seen.has(email)) return;
      seen.set(email, roleOf(pill));
    });

    return [...seen].map(([email, role], index) => ({ value: email, role, ordinal: index }));
  }

  function roleOf(pill) {
    return pill.closest('[data-test-id="compose-header-field-cc"]') ? "CC" : "TO";
  }

  function isSendButton(element) {
    return element.getAttribute("data-test-id") === "compose-send-button" && composeRootOf(element) !== null;
  }

  window.__dlpFileTransferObserverCore.register({
    channel: "YAHOO",
    recipientKind: "EMAIL",
    recipientEvidenceType: "EMAIL_IN_SEND_FORM",
    composeRootOf,
    readRecipients,
    isSendButton
    // No isWithinCompose: confirmed live that Yahoo's attach-file <input type="file"> is not a descendant of
    // compact-compose-container (it lives elsewhere in the DOM, likely a portal-rendered element), so scoping
    // the file-chosen listener by composeRootOf silently dropped every attachment. Same choice Outlook already
    // made for the same reason (see outlook.js) - every file chosen/dropped anywhere on the tab since the last
    // send is reported, which is fine for a single-purpose webmail tab.
  });
})();
