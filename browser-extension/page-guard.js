(() => {
  if (window.__companyDlpPageGuardLoaded) return;
  window.__companyDlpPageGuardLoaded = true;

  const root = () => document.documentElement;
  const uploadBlocked = () => root()?.dataset.companyDlpBlockUpload !== "false";
  const dragDropBlocked = () => root()?.dataset.companyDlpBlockDragDrop !== "false";
  const filePasteBlocked = () => root()?.dataset.companyDlpBlockFilePaste !== "false";
  const imagePasteBlocked = () => root()?.dataset.companyDlpBlockImagePaste !== "false";
  const browserScreenCaptureBlocked = () => root()?.dataset.companyDlpBlockScreenCapture !== "false";
  const recentSignals = new Map();
  let lastUserFileIntentAt = 0;
  const captureOptions = { capture: true, passive: false };

  // A plain `target[prop] = fn` assignment leaves the property writable, so a page script that runs
  // after this content script (document_start guarantees we run first, but the page's own bundle
  // runs right after) can silently reassign it and remove our interception with no error, no trace -
  // Google Gemini's own internal instrumentation does exactly this to XMLHttpRequest.prototype.send.
  //
  // Making the property non-writable/non-configurable (tried first, reverted) stops that - but it
  // also means the SAME assignment throws a TypeError in the page's own strict-mode code instead of
  // silently no-op'ing, and on Gemini that exception happened inside their app bootstrap and broke
  // the whole page. A DLP check that crashes the host page is not an acceptable trade either.
  //
  // Instead: install our guard normally (plain, writable assignment - never throws), then for a few
  // seconds after load, periodically check whether the property still points at our guard and
  // silently re-assert it if a later page script replaced it. The guard always closes over the
  // TRUE original implementation captured here at document_start (before any page script has run),
  // so re-asserting it never depends on whatever the page tried to layer on top - at worst, a page's
  // own instrumentation on that one method stops taking effect, which is far safer than a thrown
  // exception on every write attempt.
  const watchedGuards = [];

  function installGuarded(target, prop, guard) {
    try { target[prop] = guard; } catch (_) { }
    watchedGuards.push({ target, prop, guard });
  }

  // Re-check every 200ms for the lifetime of the page. This used to stop after ~5 seconds (on the
  // assumption that only a page's own initial bundle would ever reassign these) but that assumption
  // was wrong: modern SPAs commonly load feature-specific or analytics code well after initial page
  // load - lazy/code-split bundles, a chunk pulled in only once the user opens a file-attach UI, a
  // monitoring SDK initializing late - any of which can silently reassign XMLHttpRequest.prototype.
  // send/fetch/etc. Once that happened past the 5-second cutoff, our guard was gone for good with
  // nothing left to restore it, so the real upload went out completely unchecked - reproduced on
  // multiple unrelated sites, not just one app's quirk. The per-tick cost of re-checking a handful of
  // property references is negligible, so there's no real reason to ever stop.
  setInterval(() => {
    for (const { target, prop, guard } of watchedGuards) {
      try {
        if (target[prop] !== guard) target[prop] = guard;
      } catch (_) { }
    }
  }, 200);

  // Which gesture produced a given File - browser.upload (file-input picker) vs browser.drag-drop
  // (dropped onto the page) are independently grantable actions, so the transmission-time check
  // (below) needs to know which one applies to each file it finds in a fetch/XHR/form body. Files
  // with no recorded origin (e.g. constructed by page script) default to browser.upload.
  const fileOrigin = new WeakMap();
  const UPLOAD_ACTION = "browser.upload";
  const DRAG_DROP_ACTION = "browser.drag-drop";

  // Some sites never send the exact File object the user picked - WhatsApp Web, for instance,
  // encrypts media client-side before upload, so the bytes that actually hit fetch/XHR are a brand
  // new Blob the page built from scratch, never present in fileOrigin. Object-identity tagging alone
  // would silently miss every one of those. A short activity window closes that gap: any real
  // change/drop event opens a window during which an otherwise-untagged file/blob is still treated
  // as belonging to that same action, without going back to checking every page-constructed blob
  // forever (which is what caused the WAMEventBuffer.dat false positives this window replaced).
  //
  // How long this window needs to be is a real trade-off, and it was tuned wrong twice already:
  // 20s caught WhatsApp's own ~10s-cadence background telemetry too often (false alerts on an
  // unrelated blob, no real file behind them); a since-tried 4s was too SHORT the other way - a real
  // user doesn't always hit "send" within a couple seconds of picking a file (they might type a
  // message first), and once the window lapses before the real upload call fires, that call goes
  // through completely unchecked, which is a real leak, not a false alarm. A leak is the worse
  // failure mode of the two, so this errs long. To make that generous window less noisy in the
  // WhatsApp-telemetry case, markUserFileIntent() (called on genuine, continued page interaction -
  // clicks, typing, etc.) slides it forward too, so it only truly expires after the user has been
  // inactive for a while, rather than ticking down no matter what they're doing.
  const FILE_ACTIVITY_WINDOW_MS = 60000;
  let lastTaggedFileActionAt = 0;
  let lastTaggedFileActionKey = null;
  let lastTaggedFiles = [];

  function tagFileOrigin(files, actionKey) {
    for (const file of files) {
      try { fileOrigin.set(file, actionKey); } catch (_) { }
    }
    if (files.length > 0) {
      lastTaggedFileActionAt = Date.now();
      lastTaggedFileActionKey = actionKey;
      lastTaggedFiles = files.slice();
    }
  }

  function hasActiveUserGesture() {
    try {
      return navigator.userActivation?.isActive === true;
    } catch (_) {
      return false;
    }
  }

  function markUserFileIntent() {
    lastUserFileIntentAt = Date.now();
    // Slide the file-activity window forward on continued, genuine page interaction rather than
    // letting it tick down on a fixed schedule regardless of what the user is doing - a file that's
    // still pending (user is typing a message, hasn't hit send yet) shouldn't fall out of scope just
    // because some fixed number of seconds passed. Only slides an ALREADY-open window (never opens
    // one from unrelated activity with no file ever picked), so an idle gap longer than the window
    // still lets it expire normally.
    if (lastTaggedFileActionAt > 0 && (Date.now() - lastTaggedFileActionAt) <= FILE_ACTIVITY_WINDOW_MS) {
      lastTaggedFileActionAt = Date.now();
    }
  }

  function shouldNotifyUser(maxAgeMs = 8000) {
    return hasActiveUserGesture() || lastUserFileIntentAt > Date.now() - maxAgeMs;
  }

  function signal(action, details = "", notifyUser = true, resource = null, actionKey = null) {
    try {
      const now = Date.now();
      const key = `${action}|${details}|${notifyUser ? "visible" : "silent"}`;
      if ((recentSignals.get(key) || 0) > now - 2500) return;
      recentSignals.set(key, now);
      for (const [oldKey, timestamp] of recentSignals) {
        if (timestamp < now - 15000) recentSignals.delete(oldKey);
      }

      const element = root();
      if (element) {
        element.dataset.companyDlpLastBlockAction = String(action || "blocked-action");
        element.dataset.companyDlpLastBlockDetails = String(details || "");
        element.dataset.companyDlpLastBlockNotify = String(notifyUser !== false);
      }
      document.dispatchEvent(new CustomEvent("company-dlp-page-block", {
        detail: {
          action: String(action || "blocked-action"),
          details: String(details || ""),
          notifyUser: notifyUser !== false,
          resource,
          actionKey
        }
      }));
    } catch (_) { }
  }

  function stopEvent(event) {
    try { event.preventDefault(); } catch (_) { }
    try { event.stopPropagation(); } catch (_) { }
    try { event.stopImmediatePropagation(); } catch (_) { }
  }

  function blockEvent(event, action, details = "", resource = null) {
    if (event?.isTrusted !== false) markUserFileIntent();
    stopEvent(event);
    signal(action, details, true, resource);
    return false;
  }

  function isFile(value) {
    try {
      return typeof File !== "undefined" && value instanceof File;
    } catch (_) {
      return false;
    }
  }

  // A plain Blob (not a File) matters too: a site that transforms the selected file before sending
  // it - WhatsApp Web encrypts media client-side, producing a brand new, unnamed Blob - often sends
  // that Blob directly as an XHR/fetch body (not wrapped in a File/FormData entry), which File-only
  // detection would miss entirely, silently letting the real bytes through unchecked. Blob detection
  // alone would be too broad (any page-internal Blob would match) - that's why every caller of this
  // still goes through the fileOrigin/activity-window gate in checkFilesForTransmission (or the
  // synchronous equivalent for beacon/WebSocket) before actually blocking anything.
  function isBlob(value) {
    try {
      return typeof Blob !== "undefined" && value instanceof Blob;
    } catch (_) {
      return false;
    }
  }

  function isFileList(value) {
    try {
      return typeof FileList !== "undefined" && value instanceof FileList && value.length > 0;
    } catch (_) {
      return false;
    }
  }

  // WebSocket/RTCDataChannel binary protocols (WhatsApp Web's own chat/media protocol among them)
  // very commonly send raw ArrayBuffer/TypedArray frames instead of Blob objects - Blob detection
  // alone misses these completely, since neither is a Blob or File. A near-empty buffer (protocol
  // acks, pings, presence frames) isn't worth flagging on size alone, but the real backstop against
  // false positives is the same fileOrigin/activity-window gate every caller already applies before
  // treating anything found here as blockable.
  function isArrayBufferLike(value) {
    try {
      return (typeof ArrayBuffer !== "undefined" && value instanceof ArrayBuffer && value.byteLength > 8)
        || (typeof ArrayBuffer !== "undefined" && ArrayBuffer.isView(value) && value.byteLength > 8);
    } catch (_) {
      return false;
    }
  }

  // Collects every File/Blob/ArrayBuffer reachable from a fetch/XHR/beacon/share/WebSocket/RTC body
  // (FormData entries, a raw File/Blob/ArrayBuffer body, FileList, or a plain object/array containing
  // any of those, up to a shallow depth) - this is what the transmission-time check inspects, not
  // just a yes/no signal.
  function extractFiles(value, depth = 0, seen = new WeakSet(), found = []) {
    if (isFile(value) || isBlob(value) || isArrayBufferLike(value)) {
      found.push(value);
      return found;
    }
    if (isFileList(value)) {
      found.push(...Array.from(value));
      return found;
    }
    if (!value || typeof value !== "object" || depth > 3) return found;

    try {
      if (typeof FormData !== "undefined" && value instanceof FormData) {
        for (const [, entry] of value.entries()) {
          if (isFile(entry) || isBlob(entry)) found.push(entry);
        }
        return found;
      }

      if (seen.has(value)) return found;
      seen.add(value);
      if (Array.isArray(value)) {
        for (const item of value) extractFiles(item, depth + 1, seen, found);
        return found;
      }

      const prototype = Object.getPrototypeOf(value);
      if (prototype !== Object.prototype && prototype !== null) return found;
      for (const item of Object.values(value)) extractFiles(item, depth + 1, seen, found);
      return found;
    } catch (_) {
      return found;
    }
  }

  function formSelectedFiles(form) {
    try {
      return Array.from(form?.querySelectorAll?.('input[type="file"]') || [])
        .flatMap((input) => Array.from(input.files || []));
    } catch (_) {
      return [];
    }
  }

  function isFileTransfer(event) {
    try {
      if (event.dataTransfer?.files?.length) return true;
      if (Array.from(event.dataTransfer?.items || []).some((item) => item.kind === "file")) return true;
      return Array.from(event.dataTransfer?.types || []).includes("Files");
    } catch (_) {
      return false;
    }
  }

  function markFileInputs(scope = document) {
    try {
      const inputs = [];
      if (scope instanceof HTMLInputElement && scope.type === "file") inputs.push(scope);
      for (const input of scope.querySelectorAll?.('input[type="file"]') || []) inputs.push(input);
      for (const input of inputs) {
        input.dataset.companyDlpProtected = "true";
        input.setAttribute("title", "File selection is inspected by Company DLP before upload");
      }
    } catch (_) { }
  }

  function summarizeFile(file) {
    if (!file) return null;
    const name = String(file.name || "");
    const dot = name.lastIndexOf(".");
    return {
      name,
      extension: dot >= 0 ? name.slice(dot).toLowerCase() : "",
      // A raw ArrayBuffer/TypedArray (see isArrayBufferLike) has neither .name nor .size - fall back
      // to .byteLength so a binary-protocol block still reports a real size instead of 0.
      size: Number(file.size ?? file.byteLength ?? 0),
      type: String(file.type || "")
    };
  }

  function resolveFileInputFromEvent(event) {
    try {
      const path = typeof event.composedPath === "function" ? event.composedPath() : [event.target];
      for (const node of path) {
        if (node instanceof HTMLInputElement && node.type === "file") return node;
        if (node instanceof HTMLLabelElement) {
          if (node.control instanceof HTMLInputElement && node.control.type === "file") return node.control;
          const nested = node.querySelector?.('input[type="file"]');
          if (nested) return nested;
          const id = node.getAttribute?.("for");
          if (id) {
            const associated = document.getElementById(id);
            if (associated instanceof HTMLInputElement && associated.type === "file") return associated;
          }
        }
      }

      const target = event.target;
      if (target?.closest) {
        const input = target.closest('input[type="file"]');
        if (input) return input;
        const label = target.closest("label");
        if (label?.control instanceof HTMLInputElement && label.control.type === "file") return label.control;
      }
    } catch (_) { }
    return null;
  }

  // --- File hashing + the transmission-time permission check -------------------------------

  async function sha256Hex(file) {
    // Blob/File expose .arrayBuffer(); a raw ArrayBuffer/TypedArray (see isArrayBufferLike) doesn't -
    // it already IS (a view over) the bytes, so hash it directly instead.
    const buffer = typeof file?.arrayBuffer === "function" ? await file.arrayBuffer() : file;
    const digest = await crypto.subtle.digest("SHA-256", buffer);
    return Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, "0")).join("");
  }

  let requestCounter = 0;
  const pendingEvaluations = new Map();

  document.addEventListener("company-dlp-evaluate-file-response", (event) => {
    const requestId = event?.detail?.requestId;
    const resolve = pendingEvaluations.get(requestId);
    if (!resolve) return;
    pendingEvaluations.delete(requestId);
    resolve(event.detail);
  });

  // Asks content.js (which relays to the agent's real PermissionEvaluator via native messaging) for
  // a cache+grant decision on one file hash. Fails closed on any breakdown - a timeout, a missing
  // bridge, or a malformed response all resolve to "not allowed" rather than silently permitting the
  // transfer, matching the same fail-closed posture as an uncached file on the agent side.
  function evaluateFileUpload(actionKey, fileHash) {
    return new Promise((resolve) => {
      const requestId = `${Date.now()}-${++requestCounter}`;
      const timeout = setTimeout(() => {
        pendingEvaluations.delete(requestId);
        resolve({ allowed: false, reasonCode: "EvaluationTimedOut" });
      }, 10000);

      pendingEvaluations.set(requestId, (detail) => {
        clearTimeout(timeout);
        resolve({
          allowed: detail?.allowed === true,
          reasonCode: detail?.reasonCode || (detail?.allowed ? "" : "EvaluationFailed"),
          fileClassification: detail?.fileClassification || "",
          fileClassificationReasonCode: detail?.fileClassificationReasonCode || ""
        });
      });

      document.dispatchEvent(new CustomEvent("company-dlp-evaluate-file-request", {
        detail: { requestId, actionKey, fileHash }
      }));
    });
  }

  // Checks every file found in a transmission attempt (grouped by which gesture produced each one -
  // see fileOrigin) and returns whether the whole attempt may proceed. Public files never need a
  // grant; every other file needs its own hash-scoped or tier-scoped grant to be active right now.
  //
  // Every File/Blob/ArrayBuffer found in a transmission attempt gets checked here - not just ones
  // tagged via a real change/drop event or arriving within some window afterward. That gating used
  // to exist specifically to avoid alerting on WhatsApp Web's own unrelated background telemetry (a
  // small internal analytics blob it ships periodically, nothing to do with any real upload) - but
  // it's exactly what let a real file slip through uncontested: WhatsApp encrypts the selected file
  // client-side into a brand new, untagged Blob before sending it, and any transmission that fell
  // outside whatever window was configured (or used a transport the tagging logic didn't reach) went
  // out completely unchecked. An occasional alert on a harmless, unrelated blob is just noise; a real
  // file bypassing DLP entirely defeats the entire point of this tool - when those are the trade-off,
  // it always resolves in favor of checking too much rather than too little.
  function isWithinFileActivityWindow() {
    return lastTaggedFileActionAt > 0 && (Date.now() - lastTaggedFileActionAt) <= FILE_ACTIVITY_WINDOW_MS;
  }

  async function checkFilesForTransmission(files) {
    if (files.length === 0) return { allowed: true };

    const unique = Array.from(new Set(files));
    for (const rawFile of unique) {
      const actionKey = fileOrigin.get(rawFile) || lastTaggedFileActionKey || UPLOAD_ACTION;
      if ((actionKey === DRAG_DROP_ACTION && !dragDropBlocked()) || (actionKey !== DRAG_DROP_ACTION && !uploadBlocked())) {
        continue;
      }

      // A site that transforms the selected file before sending it (WhatsApp Web encrypts media
      // client-side, so the bytes on the wire are a brand new, unnamed Blob) means the transmitted
      // object itself carries neither the real filename nor a hash that matches its classification -
      // that classification was computed on the ORIGINAL plaintext by the background scanner. If this
      // exact object was never tagged but a real file was picked recently, show/hash that original
      // file instead so the alert and any resulting request reflect what the employee actually
      // selected, not an opaque "blob" - otherwise the untagged object is hashed/checked as-is.
      const file = fileOrigin.has(rawFile)
        ? rawFile
        : (isWithinFileActivityWindow() && lastTaggedFiles[0]) || rawFile;

      let hash;
      try {
        hash = await sha256Hex(file);
      } catch (_) {
        return { allowed: false, actionKey, resource: summarizeFile(file), details: "HashComputationFailed" };
      }

      const decision = await evaluateFileUpload(actionKey, hash);
      if (!decision.allowed) {
        return {
          allowed: false,
          actionKey,
          resource: {
            ...summarizeFile(file),
            sha256: hash,
            classification: decision.fileClassification,
            classificationReasonCode: decision.fileClassificationReasonCode
          },
          details: decision.reasonCode || "NotPermitted"
        };
      }
    }

    return { allowed: true };
  }

  try {
    const mediaDevices = navigator.mediaDevices;
    const originalGetDisplayMedia = mediaDevices?.getDisplayMedia?.bind(mediaDevices);
    if (originalGetDisplayMedia) {
      installGuarded(mediaDevices, "getDisplayMedia", function companyDlpGetDisplayMediaGuard() {
        if (browserScreenCaptureBlocked()) {
          signal("browser-screen-capture", "navigator.mediaDevices.getDisplayMedia", shouldNotifyUser());
          return Promise.reject(new DOMException("Screen capture blocked by company DLP policy.", "NotAllowedError"));
        }
        return originalGetDisplayMedia(...arguments);
      });
    }
  } catch (_) { }

  // Permit a user-initiated picker so Company DLP can observe the selected file metadata.
  // Script-triggered pickers without a current user gesture remain blocked. Selecting a file is no
  // longer itself the enforcement point (see checkFilesForTransmission) - this guard is unrelated to
  // file content classification, it only stops a page from silently popping a native file picker.
  try {
    const originalInputClick = HTMLInputElement.prototype.click;
    installGuarded(HTMLInputElement.prototype, "click", function companyDlpInputClick() {
      if (uploadBlocked() && String(this.type || "").toLowerCase() === "file") {
        if (!hasActiveUserGesture()) {
          signal("file-picker", this.accept || "all file types", false);
          return undefined;
        }
        markUserFileIntent();
      }
      return originalInputClick.apply(this, arguments);
    });
  } catch (_) { }

  try {
    const originalShowPicker = HTMLInputElement.prototype.showPicker;
    if (originalShowPicker) {
      installGuarded(HTMLInputElement.prototype, "showPicker", function companyDlpInputShowPicker() {
        if (uploadBlocked() && String(this.type || "").toLowerCase() === "file") {
          if (!hasActiveUserGesture()) {
            signal("showPicker", this.accept || "all file types", false);
            throw new DOMException("File selection blocked by company DLP policy.", "NotAllowedError");
          }
          markUserFileIntent();
        }
        return originalShowPicker.apply(this, arguments);
      });
    }
  } catch (_) { }

  for (const methodName of ["showOpenFilePicker", "showDirectoryPicker", "showSaveFilePicker", "chooseFileSystemEntries"]) {
    try {
      if (typeof window[methodName] !== "function") continue;
      const original = window[methodName].bind(window);
      installGuarded(window, methodName, function companyDlpPickerGuard() {
        if (uploadBlocked()) {
          const visible = shouldNotifyUser(1500);
          if (visible) markUserFileIntent();
          signal(methodName, "File System Access API", visible);
          return Promise.reject(new DOMException("Blocked by company DLP policy.", "NotAllowedError"));
        }
        return original(...arguments);
      });
    } catch (_) { }
  }

  // Files are no longer stripped out of FormData here - selection is allowed through; the
  // transmission-time checks below (fetch/XHR/submit) are the real enforcement point.
  for (const methodName of ["append", "set"]) {
    try {
      const original = FormData.prototype[methodName];
      installGuarded(FormData.prototype, methodName, function companyDlpFormDataGuard(name, value) {
        if (isFile(value)) markUserFileIntent();
        return original.apply(this, arguments);
      });
    } catch (_) { }
  }

  // originalFormSubmit/originalFormRequestSubmit are also reused by the native "submit" event
  // listener below (a plain submit-button click never calls these methods at all - the browser
  // submits the form directly - so that listener needs the real, un-overridden methods to actually
  // send the form once a check comes back allowed).
  let originalFormSubmit = null;
  let originalFormRequestSubmit = null;

  try {
    originalFormSubmit = HTMLFormElement.prototype.submit;
    installGuarded(HTMLFormElement.prototype, "submit", function companyDlpSubmitGuard() {
      const files = formSelectedFiles(this);
      if (files.length === 0) return originalFormSubmit.apply(this, arguments);

      const form = this;
      checkFilesForTransmission(files).then((result) => {
        if (result.allowed) {
          originalFormSubmit.call(form);
        } else {
          signal("form-file-submit", result.details, true, result.resource, result.actionKey);
        }
      });
      return undefined;
    });

    originalFormRequestSubmit = HTMLFormElement.prototype.requestSubmit;
    if (originalFormRequestSubmit) {
      installGuarded(HTMLFormElement.prototype, "requestSubmit", function companyDlpRequestSubmitGuard() {
        const files = formSelectedFiles(this);
        if (files.length === 0) return originalFormRequestSubmit.apply(this, arguments);

        const form = this;
        checkFilesForTransmission(files).then((result) => {
          if (result.allowed) {
            // .submit(), not .requestSubmit(), for the actual send: requestSubmit() re-dispatches a
            // "submit" event, which would loop back into this same check.
            originalFormSubmit.call(form);
          } else {
            signal("form-file-submit", result.details, true, result.resource, result.actionKey);
          }
        });
        return undefined;
      });
    }
  } catch (_) { }

  // Catches a plain submit-button click on a <form> with files selected - that path never calls
  // .submit()/.requestSubmit() at all, the browser submits the form directly once the native
  // "submit" event finishes without preventDefault(). Prevent it immediately, then resubmit via the
  // real native method if the check comes back allowed.
  window.addEventListener("submit", (event) => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !originalFormSubmit) return;
    const files = formSelectedFiles(form);
    if (files.length === 0) return;

    markUserFileIntent();
    stopEvent(event);
    checkFilesForTransmission(files).then((result) => {
      if (result.allowed) {
        try { originalFormSubmit.call(form); } catch (_) { }
      } else {
        signal("form-file-submit", result.details, true, result.resource, result.actionKey);
      }
    });
  }, captureOptions);

  try {
    const originalXhrSend = XMLHttpRequest.prototype.send;
    installGuarded(XMLHttpRequest.prototype, "send", function companyDlpXhrSendGuard(body) {
      const files = extractFiles(body);
      if (files.length === 0) return originalXhrSend.apply(this, arguments);

      const xhr = this;
      checkFilesForTransmission(files).then((result) => {
        if (result.allowed) {
          originalXhrSend.call(xhr, body);
        } else {
          signal("xhr-file-upload", result.details, true, result.resource, result.actionKey);
          // xhr.abort() alone fires no event on an XHR whose real send() was never called - the
          // page's onerror/onload/onloadend handlers (and any "uploading..." UI tied to them) would
          // then wait forever for an event that never arrives. Dispatch synthetic error/loadend
          // events instead, so a blocked upload looks like a fast, ordinary network failure to the
          // page rather than a hang.
          try { xhr.dispatchEvent(new ProgressEvent("error")); } catch (_) { }
          try { xhr.dispatchEvent(new ProgressEvent("loadend")); } catch (_) { }
        }
      });
      return undefined;
    });
  } catch (_) { }

  try {
    const originalFetch = window.fetch.bind(window);
    installGuarded(window, "fetch", async function companyDlpFetchGuard(input, init) {
      let files = extractFiles(init?.body);

      // Modern fetch also accepts a raw ReadableStream directly as init.body (with duplex: "half"),
      // for streaming a large upload without buffering the whole thing into a Blob first up front -
      // exactly the kind of thing a real media/document upload would use, and something none of the
      // File/Blob/ArrayBuffer/FormData checks above can see at all (a stream's prototype matches
      // none of them). tee() splits it into two independent readable copies before either is read:
      // one gets fully consumed here to materialize an inspectable Blob, the other is swapped into
      // init.body so the real, eventual fetch below still gets to stream the untouched original
      // bytes through - once tee() is called the original stream is locked either way, so the swap
      // happens unconditionally, not just when a file turns out to be present.
      if (files.length === 0 && typeof ReadableStream !== "undefined" && init?.body instanceof ReadableStream) {
        try {
          const [inspectStream, passThroughStream] = init.body.tee();
          init = { ...init, body: passThroughStream };
          const blob = await new Response(inspectStream).blob();
          if (blob && blob.size > 0) files = extractFiles(blob);
        } catch (_) {
          if (isWithinFileActivityWindow() && lastTaggedFiles.length > 0) {
            files = [lastTaggedFiles[0]];
          }
        }
      }

      // fetch(url, {body}) is only one calling convention - a caller can instead build a Request
      // object up front (fetch(new Request(url, {body}))), which is common behind fetch-wrapper
      // helpers/interceptor libraries. In that form the body lives on the Request instance itself,
      // as an unread ReadableStream, not in `init` (which may be entirely absent) - the check above
      // would silently miss it every time. Materialize it via a clone (so the real fetch below can
      // still read the original, untouched body) and inspect that instead.
      if (files.length === 0 && typeof Request !== "undefined" && input instanceof Request && input.body) {
        try {
          const blob = await input.clone().blob();
          if (blob && blob.size > 0) files = extractFiles(blob);
        } catch (_) {
          // Couldn't read the body - most likely its stream was already consumed/locked by the
          // page's own upload code (progress tracking, hashing, chunking libraries commonly do this)
          // before this call reached us. That's a body we KNOW exists but can't inspect, not proof
          // there's nothing there - silently falling through to allow it, like an ordinary bodyless
          // request, would open exactly the kind of blind spot this whole fetch(Request) fix exists
          // to close. Fail closed instead: if a real file was just picked, treat this unreadable
          // body as that file rather than letting it pass unexamined.
          if (isWithinFileActivityWindow() && lastTaggedFiles.length > 0) {
            files = [lastTaggedFiles[0]];
          }
        }
      }

      if (files.length === 0) return originalFetch(input, init);

      const result = await checkFilesForTransmission(files);
      if (!result.allowed) {
        signal("fetch-file-upload", result.details, true, result.resource, result.actionKey);
        throw new TypeError("File upload blocked by company DLP policy.");
      }
      return originalFetch(input, init);
    });
  } catch (_) { }

  // Neither of these two can be awaited (sendBeacon/WebSocket.send are synchronous fire-and-forget
  // contracts), so there's no room for the real evaluateFileUpload/grant lookup mid-call - a
  // relevant payload here is blocked outright rather than checked against a specific grant. Since
  // extractFiles now also matches plain Blobs (not just File instances, to catch e.g. WhatsApp's
  // client-side-encrypted media), "relevant" is scoped down to a directly-tagged file or a payload
  // arriving within the recent file-activity window - otherwise routine, file-shaped protocol
  // traffic these APIs carry all the time (WebSocket binary frames, beacon telemetry blobs) would
  // get blocked on every page, not just genuine uploads shortly after a real file selection.
  function hasRelevantFileOrBlob(data) {
    const found = extractFiles(data);
    if (found.length === 0) return false;
    return found.some((item) => fileOrigin.has(item)) || isWithinFileActivityWindow();
  }

  try {
    const originalSendBeacon = navigator.sendBeacon?.bind(navigator);
    if (originalSendBeacon) {
      installGuarded(navigator, "sendBeacon", function companyDlpBeaconGuard(url, data) {
        if (uploadBlocked() && hasRelevantFileOrBlob(data)) {
          signal("beacon-file-upload", String(url || ""), false);
          return false;
        }
        return originalSendBeacon(url, data);
      });
    }
  } catch (_) { }

  try {
    if (typeof WebSocket !== "undefined") {
      const originalWsSend = WebSocket.prototype.send;
      installGuarded(WebSocket.prototype, "send", function companyDlpWebSocketSendGuard(data) {
        if (uploadBlocked() && hasRelevantFileOrBlob(data)) {
          signal("websocket-file-upload", "", false);
          return undefined;
        }
        return originalWsSend.apply(this, arguments);
      });
    }
  } catch (_) { }

  // RTCDataChannel.send() (WebRTC peer-to-peer data channels) is another synchronous, fire-and-
  // forget send path - some chat/calling apps use it for direct P2P file transfer instead of a
  // regular HTTP upload, which would otherwise bypass every guard above entirely.
  try {
    if (typeof RTCDataChannel !== "undefined") {
      const originalRtcSend = RTCDataChannel.prototype.send;
      installGuarded(RTCDataChannel.prototype, "send", function companyDlpRtcDataChannelSendGuard(data) {
        if (uploadBlocked() && hasRelevantFileOrBlob(data)) {
          signal("rtc-file-upload", "", false);
          return undefined;
        }
        return originalRtcSend.apply(this, arguments);
      });
    }
  } catch (_) { }

  // A page can hand the selected File/Blob to a Web Worker (worker.postMessage(file)) - a
  // background JS thread that runs in its own completely separate realm. Sites commonly do this to
  // hash/encrypt/compress a file off the main thread before uploading it, and if the worker then
  // makes the actual network call itself (self.fetch(...) from inside the worker), that call never
  // touches window.fetch/XMLHttpRequest on this page at all - no content script, in any extension,
  // can ever observe or intercept code running inside a Worker's own global scope. That's not a gap
  // in this guard's coverage, it's outside what a content script can reach by design. The one place
  // still reachable is the handoff itself: postMessage() runs on the main thread, in this page's own
  // realm, before the file ever reaches the worker - blocking it here stops the file at its source
  // regardless of what the worker would have done with it. Structured-clone semantics mean the
  // File/Blob is copied to the worker, not transferred, so delaying this call for the async grant
  // check is safe - nothing about the original object becomes unusable while it awaits.
  try {
    const originalWorkerPostMessage = Worker.prototype.postMessage;
    installGuarded(Worker.prototype, "postMessage", function companyDlpWorkerPostMessageGuard(data, transferOrOptions) {
      const files = extractFiles(data);
      if (files.length === 0) return originalWorkerPostMessage.apply(this, arguments);

      const worker = this;
      const args = arguments;
      checkFilesForTransmission(files).then((result) => {
        if (result.allowed) {
          originalWorkerPostMessage.apply(worker, args);
        } else {
          signal("worker-file-transfer", result.details, true, result.resource, result.actionKey);
        }
      });
      return undefined;
    });
  } catch (_) { }

  try {
    const originalShare = navigator.share?.bind(navigator);
    if (originalShare) {
      installGuarded(navigator, "share", function companyDlpShareGuard(data) {
        if (uploadBlocked() && Array.from(data?.files || []).some(isFile)) {
          signal("web-share-file", `${data?.files?.length || 0} file(s)`, shouldNotifyUser());
          return Promise.reject(new DOMException("File sharing blocked by company DLP policy.", "NotAllowedError"));
        }
        return originalShare(data);
      });
    }
  } catch (_) { }

  // Enforcement happens right here, at drop time - not only later when the page actually tries to
  // transmit the file (see checkFilesForTransmission, still run for every fetch/XHR/form-submit as a
  // defense-in-depth backstop). A denied drop is stopped before the page's own drop handler ever
  // runs, so the file never gets a chance to appear "attached" in the UI with a working send button
  // at all. An allowed drop is replayed with a fresh DataTransfer carrying the same files, so the
  // page still sees a normal, real drop event.
  window.addEventListener("drop", (event) => {
    if (event.isTrusted === false && event.companyDlpReplay === true) return;
    if (!isFileTransfer(event)) return;

    markUserFileIntent();
    const files = Array.from(event.dataTransfer?.files || []);
    tagFileOrigin(files, DRAG_DROP_ACTION);
    if (!dragDropBlocked() || files.length === 0) return;

    const target = event.target;
    const clientX = event.clientX;
    const clientY = event.clientY;
    stopEvent(event);

    checkFilesForTransmission(files).then((result) => {
      if (result.allowed) {
        try {
          const dataTransfer = new DataTransfer();
          for (const file of files) dataTransfer.items.add(file);
          const replay = new DragEvent("drop", { bubbles: true, cancelable: true, dataTransfer, clientX, clientY });
          replay.companyDlpReplay = true;
          target.dispatchEvent(replay);
        } catch (_) { }
      } else {
        signal("file-drop", result.details, true, result.resource, result.actionKey);
      }
    });
  }, captureOptions);

  window.addEventListener("paste", (event) => {
    const files = Array.from(event.clipboardData?.files || []);
    const fileItems = Array.from(event.clipboardData?.items || []).filter((item) => item.kind === "file");
    if (!files.length && !fileItems.length) return;
    const types = files.map((file) => String(file.type || ""))
      .concat(fileItems.map((item) => String(item.type || "")));
    const isImage = types.length > 0 && types.every((type) => type.startsWith("image/"));
    if (isImage && !imagePasteBlocked()) return;
    if (!isImage && !filePasteBlocked()) return;
    blockEvent(
      event,
      isImage ? "paste-image" : "paste-file",
      `${Math.max(files.length, fileItems.length)} file(s)`,
      summarizeFile(files[0] || fileItems[0]?.getAsFile?.()));
  }, captureOptions);

  window.addEventListener("click", (event) => {
    const input = resolveFileInputFromEvent(event);
    if (input) markUserFileIntent();
  }, captureOptions);

  // Enforcement happens right here, at selection time - not only later when the page actually tries
  // to transmit the file (see checkFilesForTransmission, still run for every fetch/XHR/form-submit as
  // a defense-in-depth backstop). A denied selection is cleared from the input before the page's own
  // change handler ever runs, so the file never gets a chance to appear "attached" in the UI with a
  // working send button at all. An allowed selection is restored and replayed with a fresh change
  // event, so the page still sees a normal file selection.
  window.addEventListener("change", (event) => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || input.type !== "file") return;
    if (event.isTrusted === false && input.dataset.companyDlpReplay === "true") {
      delete input.dataset.companyDlpReplay;
      return;
    }
    if (!input.files?.length) return;

    markUserFileIntent();
    const files = Array.from(input.files);
    tagFileOrigin(files, UPLOAD_ACTION);
    if (!uploadBlocked()) return;

    stopEvent(event);
    try { input.value = ""; } catch (_) { }

    checkFilesForTransmission(files).then((result) => {
      if (result.allowed) {
        try {
          const dataTransfer = new DataTransfer();
          for (const file of files) dataTransfer.items.add(file);
          input.files = dataTransfer.files;
          input.dataset.companyDlpReplay = "true";
          input.dispatchEvent(new Event("change", { bubbles: true }));
        } catch (_) { }
      } else {
        signal("file-input-change", result.details, true, result.resource, result.actionKey);
      }
    });
  }, captureOptions);

  markFileInputs();
  try {
    new MutationObserver((records) => {
      for (const record of records) {
        for (const node of record.addedNodes || []) {
          if (node instanceof Element) markFileInputs(node);
        }
      }
    }).observe(document, { childList: true, subtree: true });
  } catch (_) { }
})();
