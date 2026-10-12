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
//   - The message box is [data-testid="conversation-compose-box-input"] (confirmed live, 2026-10-12). Pressing
//     Enter there sends the message without clicking the send button at all - this is WhatsApp Web's long-
//     standing, non-configurable behavior (unlike Telegram, which has a user preference for Ctrl+Enter instead;
//     WhatsApp has no equivalent setting). See site-observer-core.js's header for why isSendKeydown exists at
//     all: without it, this entire send path (very plausibly most real sends, since clicking the on-screen icon
//     is the less common habit) was invisible to this file until now.
//   - Confirmed live (2026-10-12) that an attachment's caption box is a SEPARATE element,
//     [data-testid="media-caption-input-container"], not conversation-compose-box-input - a real send with a
//     caption typed and Enter pressed there never matched that selector, so a naive isSendKeydown scoped to it
//     alone would have missed every captioned-attachment send (confirmed live: it fired zero console output,
//     the same silent-drop shape as a recipient or policy miss). isSendKeydown instead matches ANY
//     contenteditable textbox with role="textbox" anywhere in the document - both boxes share that shape. An
//     earlier version of this check also required the target to sit inside #main (WhatsApp's id for the whole
//     chat panel), on the assumption the caption box rendered inside it like the compose box does; that was
//     wrong too - confirmed live the caption box's Enter still didn't match with #main required, so it renders
//     outside #main entirely (likely a portal/overlay), and the #main requirement was dropped. The sidebar's
//     chat-list search box is a plain non-contenteditable <input>, so it was never at risk of matching either way.
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

  function isSendKeydown(event) {
    if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return false;
    if (event.isComposing) return false;
    return !!event.target?.closest?.('[contenteditable="true"][role="textbox"]');
  }

  window.__dlpFileTransferObserverCore.register({
    channel: "WHATSAPP",
    readRecipients,
    isSendButton,
    isSendKeydown
    // No recipientKind/recipientEvidenceType here - unlike the other adapters, WhatsApp supplies them per
    // recipient (see readRecipients) because a send can go to a 1:1 contact or a group, which carry different
    // evidence. No composeRootOf/isWithinCompose: one active conversation per page, same assumption Outlook/Teams
    // make. No attachmentNamesInWindow: not yet verified where/how sent attachments render in the message list.
  });
})();
