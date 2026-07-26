(() => {
  const actionKeySelect = document.getElementById("actionKey");
  const tierSelect = document.getElementById("tier");
  const continueButton = document.getElementById("continueButton");
  const unavailable = document.getElementById("unavailable");

  let portalBaseUrl = "";

  chrome.runtime.sendMessage({ type: "getContext" }, (response) => {
    portalBaseUrl = response?.policy?.fileClassification?.portalBaseUrl || "";
    if (!portalBaseUrl) {
      continueButton.disabled = true;
      unavailable.style.display = "block";
    }
  });

  // The tier and justification are captured here, but the actual submit happens on the portal
  // (which already has the employee's real session) - this deep link only carries the tier and
  // action, matching the same "deep link, portal does the real submit" pattern used for the
  // exact-file request path (?fromEvent=<correlationId>). No file content or file list is ever
  // read by this popup.
  continueButton.addEventListener("click", () => {
    if (!portalBaseUrl) return;
    const url = `${portalBaseUrl.replace(/\/$/, "")}/permission-requests/new`
      + `?actionKey=${encodeURIComponent(actionKeySelect.value)}&tier=${encodeURIComponent(tierSelect.value)}`;
    chrome.tabs.create({ url, active: true });
    window.close();
  });
})();
