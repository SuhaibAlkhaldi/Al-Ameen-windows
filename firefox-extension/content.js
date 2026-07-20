(() => {
  if (window.__companyDlpV2Loaded) return;
  window.__companyDlpV2Loaded = true;

  const fallbackPolicy = {
    enabled: true,
    browser: {
      blockFileUpload: true,
      blockDragAndDrop: true,
      blockFilePaste: true,
      blockImagePaste: true,
      blockSensitiveCopy: true,
      blockSensitiveInputAndSubmit: false,
      showWatermark: false
    },
    clipboard: { fragmentWindowSeconds: 300, maxFragments: 12 },
    notifications: { enabled: true, durationSeconds: 6, showRuleName: true, showBrowserPageAlerts: true, duplicateWindowSeconds: 3 },
    sensitiveRules: [
      { id: "keyword-confidential", name: "Confidential keyword", type: "Keyword", value: "confidential", enabled: true, caseSensitive: false, normalize: true, detectFragments: false },
      { id: "any-email-address", name: "Block every email address", type: "AnyEmail", value: "", enabled: true, caseSensitive: false, normalize: false, detectFragments: true }
    ]
  };

  let policy = fallbackPolicy;
  let identity = null;
  const fragmentBuffer = [];
  const safeValues = new WeakMap();
  const recentAudits = new Map();
  const recentNotices = new Map();

  function applyPageGuardPolicy() {
    const apply = () => {
      const root = document.documentElement;
      if (!root) return false;
      root.dataset.companyDlpBlockUpload = String(policy?.browser?.blockFileUpload !== false);
      root.dataset.companyDlpBlockDragDrop = String(policy?.browser?.blockDragAndDrop !== false);
      root.dataset.companyDlpBlockFilePaste = String(policy?.browser?.blockFilePaste !== false);
      root.dataset.companyDlpBlockImagePaste = String(policy?.browser?.blockImagePaste !== false);
      root.dataset.companyDlpBlockScreenCapture = String(policy?.browser?.disableBrowserScreenshots !== false);
      return true;
    };

    if (!apply()) {
      const observer = new MutationObserver(() => {
        if (apply()) observer.disconnect();
      });
      observer.observe(document, { childList: true, subtree: true });
    }
  }

  applyPageGuardPolicy();
  removeBrowserWatermark();

  function refreshContext() {
    try {
      chrome.runtime.sendMessage({ type: "getContext" }, (response) => {
        if (response?.policy) policy = response.policy;
        if (response?.identity) identity = response.identity;
        applyPageGuardPolicy();
        removeBrowserWatermark();
      });
    } catch (_) { }
  }

  refreshContext();
  setInterval(refreshContext, 5000);

  function normalize(value) {
    return String(value || "")
      .normalize("NFKC")
      .toLowerCase()
      .replace(/\[at\]|\(at\)|\{at\}|\s+at\s+/gi, "@")
      .replace(/\[dot\]|\(dot\)|\{dot\}|\s+dot\s+/gi, ".")
      .replace(/[^\p{L}\p{N}]/gu, "");
  }

  function matchText(text, includeFragments = false) {
    const source = String(text || "");
    for (const rule of policy?.sensitiveRules || []) {
      if (rule.enabled === false) continue;
      const type = String(rule.type || "").toLowerCase();
      const comparisonSource = rule.caseSensitive ? source : source.toLowerCase();
      const comparisonValue = rule.caseSensitive ? String(rule.value || "") : String(rule.value || "").toLowerCase();

      if (type === "keyword" && comparisonValue && comparisonSource.includes(comparisonValue)) return rule;
      if (type === "exactvalue" && comparisonValue) {
        const normalizedSource = normalize(source);
        const normalizedTarget = normalize(rule.value);
        const matched = rule.normalize === false
          ? comparisonSource.includes(comparisonValue)
          : normalizedSource.includes(normalizedTarget);
        if (matched) return rule;

        const minimumFragmentLength = Math.max(2, Number(rule.minimumBlockedFragmentLength || 3));
        if (rule.blockIndividualFragments === true
          && normalizedSource.length >= minimumFragmentLength
          && normalizedSource.length < normalizedTarget.length
          && normalizedTarget.includes(normalizedSource)) return { ...rule, individualFragment: true };
      }
      if (type === "regex" && rule.pattern) {
        try { if (new RegExp(rule.pattern, rule.caseSensitive ? "u" : "iu").test(source)) return rule; } catch (_) { }
      }
      if (type === "anyemail" && /\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b/i.test(source)) return rule;
    }

    if (includeFragments) return addFragmentAndDetect(source);
    return null;
  }

  function addFragmentAndDetect(text) {
    const value = normalize(text);
    if (!value) return null;
    const now = Date.now();
    const windowMs = Math.max(10, policy?.clipboard?.fragmentWindowSeconds || 300) * 1000;
    fragmentBuffer.push({ at: now, value, raw: String(text || "").replace(/\s+/g, "") });
    while (fragmentBuffer.length && fragmentBuffer[0].at < now - windowMs) fragmentBuffer.shift();
    while (fragmentBuffer.length > Math.max(2, policy?.clipboard?.maxFragments || 12)) fragmentBuffer.shift();

    for (const rule of policy?.sensitiveRules || []) {
      if (rule.enabled === false || rule.detectFragments === false || String(rule.type).toLowerCase() !== "exactvalue") continue;
      const target = normalize(rule.value);
      if (target.length < 4) continue;
      for (let start = 0; start < fragmentBuffer.length; start += 1) {
        let combined = "";
        for (let index = start; index < fragmentBuffer.length; index += 1) {
          combined += fragmentBuffer[index].value;
          if (combined.includes(target)) return { ...rule, fragmentAssembly: true };
          if (combined.length > target.length * 2) break;
        }
      }
    }

    const anyEmailRule = (policy?.sensitiveRules || []).find((rule) =>
      rule.enabled !== false && rule.detectFragments !== false && String(rule.type || "").toLowerCase() === "anyemail");
    if (anyEmailRule) {
      for (let start = 0; start < fragmentBuffer.length; start += 1) {
        let combinedRaw = "";
        for (let index = start; index < fragmentBuffer.length; index += 1) {
          combinedRaw += fragmentBuffer[index].raw || "";
          if (/\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b/i.test(combinedRaw)) {
            return { ...anyEmailRule, fragmentAssembly: true };
          }
          if (combinedRaw.length > 320) break;
        }
      }
    }
    return null;
  }

  function audit(action, result, rule, details = "", resource = null) {
    const now = Date.now();
    const duplicateWindowMs = Math.max(1, Number(policy?.notifications?.duplicateWindowSeconds || 3)) * 1000;
    const key = `${action}|${result}|${rule?.id || ""}|${details}|${location.origin}`;
    if ((recentAudits.get(key) || 0) > now - duplicateWindowMs) return;
    recentAudits.set(key, now);
    for (const [oldKey, timestamp] of recentAudits) {
      if (timestamp < now - duplicateWindowMs * 4) recentAudits.delete(oldKey);
    }

    try {
      chrome.runtime.sendMessage({
        type: "audit",
        eventType: "browser",
        action,
        result,
        ruleId: rule?.id || "",
        details,
        destination: location.origin,
        method: action,
        reasonCode: "DeniedByBrowserPolicy",
        resourceName: resource?.name || "",
        resourceExtension: resource?.extension || "",
        resourceSizeBytes: Number.isFinite(resource?.size) ? resource.size : null
      });
    } catch (_) { }
  }

  function notify(title, message, severity = "error") {
    if (policy?.notifications?.enabled === false || policy?.notifications?.showBrowserPageAlerts === false) return;

    const now = Date.now();
    const duplicateWindowMs = Math.max(1, Number(policy?.notifications?.duplicateWindowSeconds || 3)) * 1000;
    const key = `${title}|${message}|${severity}`;
    if ((recentNotices.get(key) || 0) > now - duplicateWindowMs) return;
    recentNotices.set(key, now);

    const old = document.getElementById("company-dlp-v2-notice");
    if (old) old.remove();

    const notice = document.createElement("div");
    notice.id = "company-dlp-v2-notice";
    notice.setAttribute("role", "alert");
    notice.setAttribute("aria-live", "assertive");

    const heading = document.createElement("div");
    heading.textContent = title;
    Object.assign(heading.style, { font: "700 17px Arial,sans-serif", marginBottom: "6px" });

    const body = document.createElement("div");
    body.textContent = message;
    Object.assign(body.style, { font: "500 14px/1.45 Arial,sans-serif" });

    const footer = document.createElement("div");
    footer.textContent = "Company DLP • Action blocked by company security policy";
    Object.assign(footer.style, { font: "500 11px Arial,sans-serif", opacity: ".82", marginTop: "8px" });

    notice.append(heading, body, footer);
    Object.assign(notice.style, {
      position: "fixed", zIndex: "2147483647", top: "18px", right: "18px", width: "min(430px, calc(100vw - 36px))",
      boxSizing: "border-box", background: severity === "warning" ? "#b45309" : "#b91c1c", color: "white",
      padding: "16px 18px", border: "1px solid rgba(255,255,255,.55)", borderRadius: "12px",
      boxShadow: "0 16px 40px rgba(0,0,0,.45)", pointerEvents: "none",
      transform: "translateY(-12px)", opacity: "0", transition: "transform .18s ease, opacity .18s ease"
    });
    (document.documentElement || document.body).appendChild(notice);
    requestAnimationFrame(() => { notice.style.transform = "translateY(0)"; notice.style.opacity = "1"; });

    const duration = Math.max(2, Math.min(30, Number(policy?.notifications?.durationSeconds || 6))) * 1000;
    setTimeout(() => {
      notice.style.transform = "translateY(-12px)";
      notice.style.opacity = "0";
      setTimeout(() => notice.remove(), 220);
    }, duration);
  }

  function ruleReason(rule) {
    if (!rule) return "This action is not allowed by company policy.";
    if (String(rule.type || "").toLowerCase() === "anyemail") return "Email addresses are classified as sensitive data.";
    if (policy?.notifications?.showRuleName === false) return "Sensitive data was detected.";
    return `Sensitive data rule matched: ${rule.name || "Protected content"}.`;
  }

  chrome.runtime.onMessage.addListener((message) => {
    if (message?.type !== "showDlpAlert") return;
    notify(
      message.title || "Action blocked",
      message.message || "This action is not allowed by company security policy.");
  });

  const recentBrowserBlocks = new Map();
  const recentSilentBrowserBlocks = new Map();
  const browserBlockMessages = {
    "file-picker": ["File upload blocked", "Selecting files through this browser is not allowed."],
    "showPicker": ["File upload blocked", "The browser file picker is disabled by company policy."],
    "showOpenFilePicker": ["File upload blocked", "The browser file picker is disabled by company policy."],
    "chooseFileSystemEntries": ["File upload blocked", "The legacy browser file picker is disabled by company policy."],
    "showDirectoryPicker": ["Folder access blocked", "Selecting folders through this browser is not allowed."],
    "showSaveFilePicker": ["File operation blocked", "The browser file picker is disabled by company policy."],
    "file-input-change": ["File upload blocked", "The selected file was removed before it could be uploaded."],
    "file-drop": ["File drag and drop blocked", "Dropping files into browser pages is not allowed."],
    "form-file-submit": ["File upload blocked", "The form contains a file and cannot be submitted."],
    "formdata-file": ["File upload blocked", "A page script attempted to add a file to FormData."],
    "xhr-file-upload": ["File upload blocked", "A page script attempted to upload a file using XMLHttpRequest."],
    "fetch-file-upload": ["File upload blocked", "A page script attempted to upload a file using fetch."],
    "beacon-file-upload": ["File upload blocked", "A page script attempted to send a file in the background."],
    "web-share-file": ["File sharing blocked", "Sharing files from this browser is not allowed."],
    "paste-file": ["File paste blocked", "Pasting files into browser pages is not allowed."],
    "paste-image": ["Image paste blocked", "Pasting images into browser pages is not allowed."],
    "browser-screen-capture": ["Screen sharing blocked", "Sharing or capturing the screen from this browser is not allowed."]
  };

  function browserBlockGroup(action) {
    if (action === "file-drop") return "drag-drop";
    if (action === "paste-file" || action === "paste-image") return action;
    return "file-upload";
  }

  function reportBrowserBlock(action, details = "", notifyUser = true, resource = null) {
    const now = Date.now();

    // Background page preparation can construct File/FormData objects without a user upload.
    // Keep the block in place, but suppress page alerts and heavily deduplicate silent audit events.
    if (notifyUser === false) {
      const silentKey = `${action}|${location.origin}`;
      const silentWindowMs = 60000;
      if ((recentSilentBrowserBlocks.get(silentKey) || 0) > now - silentWindowMs) return;
      recentSilentBrowserBlocks.set(silentKey, now);
      for (const [oldKey, timestamp] of recentSilentBrowserBlocks) {
        if (timestamp < now - silentWindowMs * 3) recentSilentBrowserBlocks.delete(oldKey);
      }
      audit(action, "blocked", null, details ? `silent-background:${details}` : "silent-background", resource);
      return;
    }

    const group = browserBlockGroup(action);
    const duplicateWindowMs = Math.max(12, Number(policy?.notifications?.duplicateWindowSeconds || 3)) * 1000;
    if ((recentBrowserBlocks.get(group) || 0) > now - duplicateWindowMs) return;
    recentBrowserBlocks.set(group, now);
    for (const [oldGroup, timestamp] of recentBrowserBlocks) {
      if (timestamp < now - duplicateWindowMs * 4) recentBrowserBlocks.delete(oldGroup);
    }

    const [title, message] = browserBlockMessages[action] || ["Browser action blocked", "This action is not allowed by company security policy."];
    notify(title, message);
    audit(action, "blocked", null, details, resource);
  }

  document.addEventListener("company-dlp-page-block", (event) => {
    const root = document.documentElement;
    const action = String(event?.detail?.action || root?.dataset.companyDlpLastBlockAction || "blocked-action");
    const details = String(event?.detail?.details || root?.dataset.companyDlpLastBlockDetails || "");
    const notifyUser = event?.detail?.notifyUser !== false
      && root?.dataset.companyDlpLastBlockNotify !== "false";
    reportBrowserBlock(action, details, notifyUser, event?.detail?.resource || null);
  }, true);

  function stop(event) {
    event.preventDefault();
    event.stopPropagation();
    event.stopImmediatePropagation();
  }

  function resolveFileInput(event) {
    try {
      const path = typeof event.composedPath === "function" ? event.composedPath() : [event.target];
      for (const node of path) {
        if (node instanceof HTMLInputElement && node.type === "file") return node;
        if (node instanceof HTMLLabelElement && node.control instanceof HTMLInputElement && node.control.type === "file") return node.control;
      }
      const target = event.target;
      if (target?.closest) {
        const direct = target.closest('input[type="file"]');
        if (direct) return direct;
        const label = target.closest("label");
        if (label?.control instanceof HTMLInputElement && label.control.type === "file") return label.control;
      }
    } catch (_) { }
    return null;
  }

  document.addEventListener("change", (event) => {
    if (policy?.browser?.blockFileUpload === false) return;
    const input = event.target;
    if (!(input instanceof HTMLInputElement) || input.type !== "file" || !input.files?.length) return;
    const count = input.files.length;
    const resource = summarizeFile(input.files[0]);
    try { input.value = ""; } catch (_) { }
    stop(event);
    reportBrowserBlock("file-input-change", `${count} file(s)`, true, resource);
  }, true);

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

  function containsDraggedFiles(event) {
    try {
      if (event.dataTransfer?.files?.length) return true;
      if (Array.from(event.dataTransfer?.items || []).some((item) => item.kind === "file")) return true;
      return Array.from(event.dataTransfer?.types || []).includes("Files");
    } catch (_) {
      return false;
    }
  }

  for (const eventName of ["dragenter", "dragover", "drop"]) {
    const handler = (event) => {
      if (policy?.browser?.blockDragAndDrop === false || !containsDraggedFiles(event)) return;
      stop(event);
      if (eventName === "drop") {
        const files = Array.from(event.dataTransfer?.files || []);
        reportBrowserBlock("file-drop", `${files.length} file(s)`, true, summarizeFile(files[0]));
      }
    };
    window.addEventListener(eventName, handler, { capture: true, passive: false });
  }

  document.addEventListener("copy", (event) => {
    if (policy?.browser?.blockSensitiveCopy === false) return;
    const text = document.getSelection()?.toString() || getSelectedInputText(event.target);
    const rule = matchText(text, true);
    if (!rule) return;
    stop(event);
    event.clipboardData?.setData("text/plain", "");
    notify(
      "Sensitive data copy blocked",
      rule.fragmentAssembly
        ? "The copied content completes sensitive data assembled from multiple copy operations."
        : rule.individualFragment
          ? "The selected text is a protected fragment of sensitive data."
          : ruleReason(rule));
    audit("copy-sensitive", "blocked", rule, rule.fragmentAssembly ? "fragment-assembly" : rule.individualFragment ? "individual-fragment" : "rule-match");
  }, true);

  document.addEventListener("paste", (event) => {
    const files = Array.from(event.clipboardData?.files || []);
    if (files.length) {
      const resource = summarizeFile(files[0]);
      const isImage = files.every((file) => String(file.type || "").startsWith("image/"));
      const shouldBlock = isImage
        ? policy?.browser?.blockImagePaste !== false
        : policy?.browser?.blockFilePaste !== false;
      if (shouldBlock) {
        stop(event);
        reportBrowserBlock(isImage ? "paste-image" : "paste-file", `${files.length} file(s)`, true, resource);
        return;
      }
    }

    if (policy?.browser?.blockSensitiveInputAndSubmit === false) return;
    const target = event.target;
    const pasted = event.clipboardData?.getData("text/plain") || "";
    const candidate = candidateText(target, pasted);
    const rule = matchText(candidate, false);
    if (!rule) return;
    stop(event);
    notify("Sensitive data paste blocked", ruleReason(rule));
    audit("paste-sensitive", "blocked", rule);
  }, true);

  document.addEventListener("beforeinput", (event) => {
    if (policy?.browser?.blockSensitiveInputAndSubmit === false || event.isComposing) return;
    if (!event.data || !isEditable(event.target)) return;
    const candidate = candidateText(event.target, event.data);
    const rule = matchText(candidate, false);
    if (!rule) return;
    stop(event);
    notify("Sensitive data entry blocked", ruleReason(rule));
    audit("typed-sensitive", "blocked", rule);
  }, true);

  document.addEventListener("focusin", (event) => {
    if (isEditable(event.target)) safeValues.set(event.target, readEditable(event.target));
  }, true);

  document.addEventListener("input", (event) => {
    if (policy?.browser?.blockSensitiveInputAndSubmit === false || !isEditable(event.target)) return;
    const target = event.target;
    const current = readEditable(target);
    const rule = matchText(current, false);
    if (!rule) {
      safeValues.set(target, current);
      return;
    }
    writeEditable(target, safeValues.get(target) || "");
    notify("Sensitive data removed", ruleReason(rule));
    audit("input-sensitive", "blocked", rule);
  }, true);

  document.addEventListener("formdata", (event) => {
    if (policy?.browser?.blockFileUpload === false) return;
    try {
      for (const [key, value] of event.formData.entries()) {
        if (value instanceof File) event.formData.delete(key);
      }
    } catch (_) { }
    // Do not alert merely because a site prepared FormData in the background.
    // Picker, drop, paste, change and real user-triggered send paths create the visible alert.
  }, true);

  document.addEventListener("submit", (event) => {
    if (policy?.browser?.blockSensitiveInputAndSubmit === false) return;
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const values = Array.from(form.querySelectorAll('input:not([type="password"]), textarea, [contenteditable="true"]'))
      .map(readEditable).join("\n");
    const rule = matchText(values, false);
    if (!rule) return;
    stop(event);
    notify("Form submission blocked", ruleReason(rule));
    audit("form-submit-sensitive", "blocked", rule);
  }, true);

  function isEditable(target) {
    return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target?.isContentEditable === true;
  }

  function readEditable(target) {
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) return target.value || "";
    return target?.textContent || "";
  }

  function writeEditable(target, value) {
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) target.value = value;
    else if (target?.isContentEditable) target.textContent = value;
  }

  function candidateText(target, inserted) {
    if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) {
      const start = target.selectionStart ?? target.value.length;
      const end = target.selectionEnd ?? start;
      return target.value.slice(0, start) + inserted + target.value.slice(end);
    }
    if (target?.isContentEditable) return (target.textContent || "") + inserted;
    return inserted;
  }

  function getSelectedInputText(target) {
    if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return "";
    const start = target.selectionStart ?? 0;
    const end = target.selectionEnd ?? 0;
    return target.value.slice(start, end);
  }

  function removeBrowserWatermark() {
    document.querySelectorAll('[id="company-dlp-v2-watermark"]').forEach((node) => node.remove());
  }

})();
