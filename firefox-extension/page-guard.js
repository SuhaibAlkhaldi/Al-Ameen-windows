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
  const blockedFormData = new WeakSet();
  let lastUserFileIntentAt = 0;

  function hasActiveUserGesture() {
    try {
      return navigator.userActivation?.isActive === true;
    } catch (_) {
      return false;
    }
  }

  function markUserFileIntent() {
    lastUserFileIntentAt = Date.now();
  }

  function shouldNotifyUser(maxAgeMs = 8000) {
    return hasActiveUserGesture() || lastUserFileIntentAt > Date.now() - maxAgeMs;
  }

  function signal(action, details = "", notifyUser = true, resource = null) {
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
          resource
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

  function isFileList(value) {
    try {
      return typeof FileList !== "undefined" && value instanceof FileList && value.length > 0;
    } catch (_) {
      return false;
    }
  }

  function containsFilePayload(value, depth = 0, seen = new WeakSet()) {
    if (isFile(value) || isFileList(value)) return true;
    if (!value || typeof value !== "object" || depth > 3) return false;

    try {
      if (typeof FormData !== "undefined" && value instanceof FormData) {
        if (blockedFormData.has(value)) return true;
        for (const [, entry] of value.entries()) {
          if (isFile(entry)) return true;
        }
        return false;
      }

      if (seen.has(value)) return false;
      seen.add(value);
      if (Array.isArray(value)) {
        return value.some((item) => containsFilePayload(item, depth + 1, seen));
      }

      const prototype = Object.getPrototypeOf(value);
      if (prototype !== Object.prototype && prototype !== null) return false;
      return Object.values(value).some((item) => containsFilePayload(item, depth + 1, seen));
    } catch (_) {
      return false;
    }
  }

  function formContainsSelectedFiles(form) {
    try {
      return Array.from(form?.querySelectorAll?.('input[type="file"]') || [])
        .some((input) => input.files && input.files.length > 0);
    } catch (_) {
      return false;
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
      size: Number(file.size || 0),
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

  try {
    const mediaDevices = navigator.mediaDevices;
    const originalGetDisplayMedia = mediaDevices?.getDisplayMedia?.bind(mediaDevices);
    if (originalGetDisplayMedia) {
      mediaDevices.getDisplayMedia = function companyDlpGetDisplayMediaGuard() {
        if (browserScreenCaptureBlocked()) {
          signal("browser-screen-capture", "navigator.mediaDevices.getDisplayMedia", shouldNotifyUser());
          return Promise.reject(new DOMException("Screen capture blocked by company DLP policy.", "NotAllowedError"));
        }
        return originalGetDisplayMedia(...arguments);
      };
    }
  } catch (_) { }

  // Permit a user-initiated picker so Company DLP can observe the selected file metadata.
  // Script-triggered pickers without a current user gesture remain blocked. The selected file
  // is stopped at the capture-phase change handler before the page receives it.
  try {
    const originalInputClick = HTMLInputElement.prototype.click;
    HTMLInputElement.prototype.click = function companyDlpInputClick() {
      if (uploadBlocked() && String(this.type || "").toLowerCase() === "file") {
        if (!hasActiveUserGesture()) {
          signal("file-picker", this.accept || "all file types", false);
          return undefined;
        }
        markUserFileIntent();
      }
      return originalInputClick.apply(this, arguments);
    };
  } catch (_) { }

  try {
    const originalShowPicker = HTMLInputElement.prototype.showPicker;
    if (originalShowPicker) {
      HTMLInputElement.prototype.showPicker = function companyDlpInputShowPicker() {
        if (uploadBlocked() && String(this.type || "").toLowerCase() === "file") {
          if (!hasActiveUserGesture()) {
            signal("showPicker", this.accept || "all file types", false);
            throw new DOMException("File selection blocked by company DLP policy.", "NotAllowedError");
          }
          markUserFileIntent();
        }
        return originalShowPicker.apply(this, arguments);
      };
    }
  } catch (_) { }

  for (const methodName of ["showOpenFilePicker", "showDirectoryPicker", "showSaveFilePicker", "chooseFileSystemEntries"]) {
    try {
      if (typeof window[methodName] !== "function") continue;
      const original = window[methodName].bind(window);
      Object.defineProperty(window, methodName, {
        configurable: true,
        writable: true,
        value: function companyDlpPickerGuard() {
          if (uploadBlocked()) {
            const visible = shouldNotifyUser(1500);
            if (visible) markUserFileIntent();
            signal(methodName, "File System Access API", visible);
            return Promise.reject(new DOMException("Blocked by company DLP policy.", "NotAllowedError"));
          }
          return original(...arguments);
        }
      });
    } catch (_) { }
  }

  // Remove real File values from FormData, but do not alert merely because a page prepared
  // an upload object in the background. A user-facing alert comes from picker/drop/paste or
  // from an actual send that occurs during a recent user gesture.
  for (const methodName of ["append", "set"]) {
    try {
      const original = FormData.prototype[methodName];
      FormData.prototype[methodName] = function companyDlpFormDataGuard(name, value) {
        if (uploadBlocked() && isFile(value)) {
          blockedFormData.add(this);
          return undefined;
        }
        return original.apply(this, arguments);
      };
    } catch (_) { }
  }

  try {
    const originalSubmit = HTMLFormElement.prototype.submit;
    HTMLFormElement.prototype.submit = function companyDlpSubmitGuard() {
      if (uploadBlocked() && formContainsSelectedFiles(this)) {
        signal("form-file-submit", "Programmatic form submission", false);
        return undefined;
      }
      return originalSubmit.apply(this, arguments);
    };

    const originalRequestSubmit = HTMLFormElement.prototype.requestSubmit;
    if (originalRequestSubmit) {
      HTMLFormElement.prototype.requestSubmit = function companyDlpRequestSubmitGuard() {
        if (uploadBlocked() && formContainsSelectedFiles(this)) {
          signal("form-file-submit", "Programmatic requestSubmit", false);
          return undefined;
        }
        return originalRequestSubmit.apply(this, arguments);
      };
    }
  } catch (_) { }

  try {
    const originalXhrSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.send = function companyDlpXhrSendGuard(body) {
      if (uploadBlocked() && containsFilePayload(body)) {
        signal("xhr-file-upload", "XMLHttpRequest contains File data", false);
        try { this.abort(); } catch (_) { }
        return undefined;
      }
      return originalXhrSend.apply(this, arguments);
    };
  } catch (_) { }

  try {
    const originalFetch = window.fetch.bind(window);
    window.fetch = async function companyDlpFetchGuard(input, init) {
      if (uploadBlocked() && containsFilePayload(init?.body)) {
        signal("fetch-file-upload", "fetch contains File data", false);
        throw new TypeError("File upload blocked by company DLP policy.");
      }
      return originalFetch(input, init);
    };
  } catch (_) { }

  try {
    const originalSendBeacon = navigator.sendBeacon?.bind(navigator);
    if (originalSendBeacon) {
      navigator.sendBeacon = function companyDlpBeaconGuard(url, data) {
        if (uploadBlocked() && containsFilePayload(data)) {
          signal("beacon-file-upload", String(url || ""), false);
          return false;
        }
        return originalSendBeacon(url, data);
      };
    }
  } catch (_) { }

  try {
    const originalShare = navigator.share?.bind(navigator);
    if (originalShare) {
      navigator.share = function companyDlpShareGuard(data) {
        if (uploadBlocked() && Array.from(data?.files || []).some(isFile)) {
          signal("web-share-file", `${data?.files?.length || 0} file(s)`, shouldNotifyUser());
          return Promise.reject(new DOMException("File sharing blocked by company DLP policy.", "NotAllowedError"));
        }
        return originalShare(data);
      };
    }
  } catch (_) { }

  const captureOptions = { capture: true, passive: false };

  // Prevent file drag/drop everywhere. Intermediate drag events are silent; the final drop
  // produces one user-facing alert.
  for (const eventName of ["dragenter", "dragover", "drop"]) {
    window.addEventListener(eventName, (event) => {
      if (!dragDropBlocked() || !isFileTransfer(event)) return;
      stopEvent(event);
      if (eventName === "drop") {
        markUserFileIntent();
        const files = Array.from(event.dataTransfer?.files || []);
        signal("file-drop", `${files.length} file(s)`, true, summarizeFile(files[0]));
      }
    }, captureOptions);
  }

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
    if (!uploadBlocked()) return;
    const input = resolveFileInputFromEvent(event);
    if (input) markUserFileIntent();
  }, captureOptions);

  window.addEventListener("change", (event) => {
    if (!uploadBlocked()) return;
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || input.type !== "file" || !input.files?.length) return;
    const files = Array.from(input.files);
    const resource = summarizeFile(files[0]);
    try { input.value = ""; } catch (_) { }
    blockEvent(event, "file-input-change", `${files.length} file(s)`, resource);
  }, captureOptions);

  window.addEventListener("submit", (event) => {
    if (!uploadBlocked()) return;
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !formContainsSelectedFiles(form)) return;
    blockEvent(event, "form-file-submit", "Form contains selected file(s)");
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
