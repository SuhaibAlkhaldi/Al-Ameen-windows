// Phase 7: Telegram Web (the "K" client, web.telegram.org/k/) adapter for site-observer-core.js. Runs on
// web.telegram.org only, after site-observer-core.js (declared first for this match in manifest.json).
//
// Verified live on web.telegram.org/k (2026-10-12) in a real chat:
//   - Only one chat is ever open at a time in this client (no simultaneous compose popups like Gmail/Yahoo), so
//     there is nothing to scope a compose boundary by - no composeRootOf/isWithinCompose, same choice already
//     made for Outlook for the same reason.
//   - The open chat's title renders as a single element in the whole document: a
//     `span.peer-title` inside `#column-center .chat-info` (there are other `.peer-title` spans in the sidebar's
//     own chat list, so the query must stay scoped to #column-center - an unscoped query can match the wrong
//     one). Its `data-peer-id` is Telegram's own chat id and its text is the display name shown in the header.
//   - Telegram's id scheme (confirmed live across a 1:1 "Saved Messages" chat, id 1100716497, and ~10 groups/
//     channels in the sidebar, all negative ids) is positive for a 1:1 user chat, negative for a group,
//     supergroup or channel - used here directly to decide CHAT_NAME vs GROUP_NAME, reusing the exact kind/
//     evidence pairing WhatsApp's adapter already activated (CHAT_NAME/CHAT_CONTACT_NAME,
//     GROUP_NAME/GROUP_NAME) - no backend change needed beyond adding "TELEGRAM" to AllowedChannels.
//   - The attach-file <input type="file"> lives inside .chat-input, unlike Yahoo's portal-rendered one, so no
//     isWithinCompose guard was needed to catch it.
//   - The send button's own class list always includes "btn-send", regardless of UI language.
//   - Sending by pressing Enter in the message box does NOT dispatch a click on the send button at all -
//     confirmed live by attaching a capturing document click listener before pressing Enter and seeing it never
//     fire, while the message was genuinely sent. This is why adapter.isSendKeydown exists (see
//     site-observer-core.js's header) - without it, Enter-to-send (the default and most common way to send on
//     this site) would be entirely invisible. Only plain Enter (no Shift/Ctrl/Alt/Meta) is matched: Telegram has
//     a user preference for "Ctrl+Enter to send" instead of plain Enter, which was not exercised live - under
//     that preference this adapter under-detects (misses the real Ctrl+Enter send) rather than over-detects, a
//     deliberate, safer choice than treating Ctrl+Enter as always-send, which would risk misrecording a plain
//     Enter newline as a send under that same preference. Known gap, not fixed.
//   - No selector was found for a per-chat attachment-chip list, so attachmentNamesInWindow is omitted and every
//     hashed attachment since the last send is reported, same open point already documented for Outlook, Teams
//     and Yahoo - see the Phase 7 plan's open points.
(() => {
  "use strict";

  function readRecipients() {
    const title = document.querySelector("#column-center .chat-info .peer-title");
    const value = (title?.textContent || "").trim();
    const peerId = title?.getAttribute("data-peer-id");
    if (!value || !peerId) return [];

    const isGroup = peerId.startsWith("-");
    return [{
      value,
      role: null,
      ordinal: 0,
      kind: isGroup ? "GROUP_NAME" : "CHAT_NAME",
      evidenceType: isGroup ? "GROUP_NAME" : "CHAT_CONTACT_NAME"
    }];
  }

  function isSendButton(element) {
    return element.classList.contains("btn-send");
  }

  function isSendKeydown(event) {
    if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return false;
    if (event.isComposing) return false;
    return event.target?.closest?.(".input-message-input") !== null;
  }

  window.__dlpFileTransferObserverCore.register({
    channel: "TELEGRAM",
    readRecipients,
    isSendButton,
    isSendKeydown
  });
})();
