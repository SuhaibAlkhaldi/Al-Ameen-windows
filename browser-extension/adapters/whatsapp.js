// Phase 7: WhatsApp Web adapter for site-observer-core.js. Runs on web.whatsapp.com.
//
// Verified live on web.whatsapp.com, the user's own logged-in session (2026-10-10), read-only DOM inspection only:
//   - The open chat's header title is [data-testid="conversation-info-header"]
//     [data-testid="conversation-info-header-chat-title"]. For a 1:1 chat this is the contact's saved name, or the
//     raw phone number if the contact isn't saved (confirmed consistent with how an unsaved member renders inside
//     the Group Info panel, e.g. "~+966 55 068 1870" - the "~" marks a self-set name for someone not in your
//     contacts). For a group chat this is the group's own name.
//   - A group chat's header also renders [data-testid="chat-subtitle"] (a truncated, comma-separated summary of
//     first names, e.g. "Abu, Jameel, Rawan, Yousef, +966 55 068 1870, You" - not used for anything here, only its
//     presence matters). This was absent on the one 1:1 chat checked, so its presence is used to tell a group chat
//     from a 1:1 chat. Not yet re-confirmed against a group with a custom photo or an unsaved 1:1 contact.
//   - Full per-member names (e.g. "Abu Hisham Unlimited-innovation", "Jameel UI") are only available by opening the
//     "Group info" side panel ([data-testid="group-info-participants-section"]), which does not happen on every
//     send - confirmed the chat-subtitle summary above is all that's available without it. That's why a group's
//     recipient is recorded as the group name alone (RecipientKind=GROUP_NAME, LOW evidence), not a per-member
//     roster like the Teams adapter - the full roster simply isn't on the page at send time.
//   - "Document" and "Photos & videos" (the attach menu's two relevant options) both end up firing a "change" event
//     on an <input type="file">, which site-observer-core.js's existing global change listener already catches
//     regardless of which path was used - no adapter code needed for that part, same as Gmail/Outlook/Teams.
//   - The send button itself has no stable data-testid (only generated CSS classes plus aria-label="Send", which
//     could read differently if the account's UI language isn't English), but its inner icon does:
//     [data-testid="wds-ic-send-filled"]. Icon names aren't localized, so isSendButton checks for that icon inside
//     the clicked button instead of relying on the aria-label.
//
// Not yet verified: whether sending the very first-ever message to a brand-new contact uses a different send
// control, the way Teams surprisingly did for a brand-new chat (newMessageCommands-send vs sendMessageCommands-send).
// WhatsApp has no "chat request must be accepted" concept like Teams, so this is less likely, but it hasn't been
// tested live - worth a live check the same way Teams' gap was found.
(() => {
  "use strict";

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

  window.__dlpFileTransferObserverCore.register({
    channel: "WHATSAPP",
    readRecipients,
    isSendButton
    // No recipientKind/recipientEvidenceType here - unlike the other adapters, WhatsApp supplies them per
    // recipient (see readRecipients) because a send can go to a 1:1 contact or a group, which carry different
    // evidence. No composeRootOf/isWithinCompose: one active conversation per page, same assumption Outlook/Teams
    // make. No attachmentNamesInWindow: not yet verified where/how sent attachments render in the message list.
  });
})();
