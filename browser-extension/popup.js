(() => {
  const requestTypeSelect = document.getElementById("requestType");
  const fileFields = document.getElementById("fileFields");
  const extensionFields = document.getElementById("extensionFields");
  const actionKeySelect = document.getElementById("actionKey");
  const tierSelect = document.getElementById("tier");
  const extensionIdInput = document.getElementById("extensionId");
  const continueButton = document.getElementById("continueButton");
  const unavailable = document.getElementById("unavailable");

  // Chrome/Edge Web Store extension ids are always exactly 32 lowercase letters a-p - see Chrome's
  // own "key" manifest field documentation. Mirrors
  // DLPManagementSystem.Common.ExtensionActionProvisioner.IsValidExtensionId on the backend, which is
  // the real enforcement point - this is just an early, friendlier check before the portal round-trip.
  const EXTENSION_ID_PATTERN = /^[a-p]{32}$/;

  let portalBaseUrl = "";

  chrome.runtime.sendMessage({ type: "getContext" }, (response) => {
    portalBaseUrl = response?.policy?.fileClassification?.portalBaseUrl || "";
    if (!portalBaseUrl) {
      continueButton.disabled = true;
      unavailable.style.display = "block";
    }
  });

  function isExtensionMode() {
    return requestTypeSelect.value === "extension";
  }

  function updateFieldVisibility() {
    const extensionMode = isExtensionMode();
    fileFields.style.display = extensionMode ? "none" : "block";
    extensionFields.style.display = extensionMode ? "block" : "none";
  }

  requestTypeSelect.addEventListener("change", updateFieldVisibility);
  updateFieldVisibility();

  // The actual submit always happens on the portal (which already has the employee's real session) -
  // this deep link only carries enough to pre-fill the right request there, matching the same
  // "deep link, portal does the real submit" pattern used for the exact-file request path
  // (?fromEvent=<correlationId>). No file content, file list, or installed-extension list is ever
  // read by this popup.
  continueButton.addEventListener("click", () => {
    if (!portalBaseUrl) return;
    const base = portalBaseUrl.replace(/\/$/, "");

    if (isExtensionMode()) {
      const extensionId = extensionIdInput.value.trim().toLowerCase();
      if (!EXTENSION_ID_PATTERN.test(extensionId)) {
        extensionIdInput.focus();
        return;
      }
      const url = `${base}/permission-requests/new?requestType=extension&extensionId=${encodeURIComponent(extensionId)}`;
      chrome.tabs.create({ url, active: true });
      window.close();
      return;
    }

    const url = `${base}/permission-requests/new`
      + `?actionKey=${encodeURIComponent(actionKeySelect.value)}&tier=${encodeURIComponent(tierSelect.value)}`;
    chrome.tabs.create({ url, active: true });
    window.close();
  });
})();
