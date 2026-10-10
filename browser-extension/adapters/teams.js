// Phase 7: Microsoft Teams (web) adapter for site-observer-core.js. Runs on teams.microsoft.com (work/school tenants)
// and teams.live.com (personal tenant) - confirmed live (2026-10-09) that Microsoft serves these as two different
// domains for the same product, switched from the account menu's tenant picker, not just two URLs for one app.
//
// Verified live on teams.live.com, Personal tenant (2026-10-09), in a real chat (the account's own self-chat, plus
// the chat header's participant markup, which is identical in shape for a 1:1 or group chat):
//   - A Teams chat has no To/Cc fields: its recipients are the chat's existing members. They are read from the open
//     chat's own header, where every member - including the signed-in user - renders as an element (an <li> in an
//     established chat; some other tag, seen as a <span>-like element, in a brand-new chat with no message history
//     yet - confirmed live 2026-10-10, so the selector matches on data-tid alone, not the tag) with
//     data-tid="participant-<per-person id>", whose plain text content is the member's display name (e.g.
//     "Ahmad Khaled"). This is a per-member element, including in a group chat, so group members are read
//     individually by name rather than lumped under one group label - RecipientKind stays CHAT_NAME like a 1:1 chat.
//   - The signed-in user's own entry is the only one whose name ends with the literal suffix " (You)" - e.g.
//     "Suhaib Alkhaldi (You)" - and is excluded on that basis. There is no other reliable "is this me" signal in
//     this markup (the data-tid's id suffix is the account's own Teams identity string, not something to hardcode).
//   - The attach control (data-tid="sendMessageCommands-FilePicker") opens a menu with two different paths:
//     "Upload from this device" creates a real input[type=file][multiple] the moment it is clicked - confirmed live -
//     which site-observer-core.js's existing global "change" listener already catches, same as Gmail/Outlook.
//     "Attach cloud files" shares a OneDrive link instead; no local file is ever chosen, no input[type=file] fires,
//     and no bytes ever reach the browser to hash. THIS PATH IS NOT OBSERVED. A file shared that way produces no
//     FileTransferEvent at all - a real, silent gap, not an oversight, and there is no DOM signal known yet that
//     would let this adapter even detect that it happened.
//   - The send control is button[data-tid="sendMessageCommands-send"] (aria-label "Send (Ctrl+Enter)") in an existing
//     conversation. A brand-new 1:1 chat (no prior message history, e.g. right after the recipient accepted a chat
//     request) renders a different toolbar whose send button is button[data-tid="newMessageCommands-send"] instead -
//     confirmed live (2026-10-10) via a real send that silently did nothing until this second data-tid was added.
//   - The message box is a contenteditable div (data-tid="ckeditor"); not read by this adapter, same as the other
//     two - only recipients, attachments and the send click matter here.
//
// Not yet verified: a chat with many members may collapse the overflow into a single combined avatar that is not
// individually data-tid-tagged per person, in which case those members would be silently missed - this needs a live
// check against a real large group chat. Pop-out chat windows (Teams can open a chat in its own window) are not
// handled - this adapter assumes one active conversation in the page, same assumption Outlook's adapter makes.
// teams.cloud.microsoft, a URL migration Microsoft is previewing in-product as of this writing, is not yet live and
// is not in manifest.json's matches - add it once Teams actually serves the product there.
(() => {
  "use strict";

  const SELF_SUFFIX = " (You)";

  function readRecipients() {
    const seen = new Map();
    document.querySelectorAll('[data-tid^="participant-"]').forEach(element => {
      const identity = element.getAttribute("data-tid");
      const name = (element.textContent || "").trim();
      if (!name || name.endsWith(SELF_SUFFIX)) return;
      if (!seen.has(identity)) seen.set(identity, name);
    });

    return [...seen.values()].map((name, index) => ({ value: name, role: null, ordinal: index }));
  }

  const SEND_BUTTON_DATA_TIDS = new Set(["sendMessageCommands-send", "newMessageCommands-send"]);

  function isSendButton(element) {
    return SEND_BUTTON_DATA_TIDS.has(element.getAttribute("data-tid"));
  }

  window.__dlpFileTransferObserverCore.register({
    channel: "TEAMS",
    recipientKind: "CHAT_NAME",
    recipientEvidenceType: "TEAMS_CHAT_NAME",
    readRecipients,
    isSendButton
    // No composeRootOf: one active conversation per page, same assumption as Outlook's adapter.
    // No attachmentNamesInWindow: not yet verified where/how sent attachments render in the message list - see the
    // file header. No isWithinCompose: the whole page is the one conversation, nothing further to scope by.
  });
})();
