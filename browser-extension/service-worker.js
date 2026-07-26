const NATIVE_HOST = "com.company.dlp";
const DOWNLOAD_ACTION = "browser.download";

function isDevelopmentHttpFallbackEnabled() {
  return new Promise((resolve) => {
    try {
      chrome.storage.managed.get(["developmentHttpFallback"], (values) => {
        if (chrome.runtime.lastError) {
          resolve(false);
          return;
        }
        resolve(values?.developmentHttpFallback === true);
      });
    } catch (_) {
      resolve(false);
    }
  });
}

async function sendDevelopmentFallback(message) {
  try {
    const response = await fetch("http://127.0.0.1:5055/api/v1/development/native-message", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(message),
      cache: "no-store"
    });
    if (!response.ok) return { success: false, message: `Development bridge returned HTTP ${response.status}` };
    return await response.json();
  } catch (error) {
    return { success: false, message: `Development bridge unavailable: ${String(error)}` };
  }
}

async function sendNative(message) {
  const nativeResponse = await new Promise((resolve) => {
    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST, message, (response) => {
        if (chrome.runtime.lastError) {
          resolve({
            success: false,
            message: chrome.runtime.lastError.message,
            nativeTransportFailure: true
          });
          return;
        }
        resolve(response || {
          success: false,
          message: "Empty native response",
          nativeTransportFailure: true
        });
      });
    } catch (error) {
      resolve({
        success: false,
        message: String(error),
        nativeTransportFailure: true
      });
    }
  });

  if (nativeResponse?.nativeTransportFailure === true
      && await isDevelopmentHttpFallbackEnabled()) {
    return await sendDevelopmentFallback(message);
  }

  return nativeResponse;
}

function normalizeGrant(grant) {
  return {
    actionKey: grant.actionKey,
    allowed: grant.allowed === true,
    subjectType: grant.subjectType,
    subjectId: (grant.subjectId ?? "").trim(),
    source: grant.source,
    priority: grant.priority ?? 100,
    startsAtUtc: grant.startsAtUtc,
    expiresAtUtc: grant.expiresAtUtc ?? null,
    revokedAtUtc: grant.revokedAtUtc ?? null,
    createdAtUtc: grant.createdAtUtc,
    grantId: grant.grantId
  };
}

function parsedTime(value) {
  if (!value) return null;
  const time = Date.parse(value);
  return Number.isNaN(time) ? null : time;
}

function subjectSpecificity(subjectType) {
  switch (subjectType) {
    case "UserSid": return 50;
    case "Username": return 40;
    case "DeviceId": return 30;
    case "MachineName": return 20;
    case "Group": return 10;
    case "Department": return 5;
    default: return 0;
  }
}

function matchesSubject(grant, identity) {
  const expected = grant.subjectId;
  switch (grant.subjectType) {
    case "Global": return expected === "" || expected === "*";
    case "UserSid": return expected.toLowerCase() === (identity.userSid || "").toLowerCase();
    case "Username": return expected.toLowerCase() === (identity.username || "").toLowerCase();
    case "MachineName": return expected.toLowerCase() === (identity.machineName || "").toLowerCase();
    default: return false;
  }
}

function isGrantActive(grant, nowMs) {
  if (grant.revokedAtUtc) return false;
  const starts = parsedTime(grant.startsAtUtc);
  if (starts !== null && starts > nowMs) return false;
  const expires = parsedTime(grant.expiresAtUtc);
  if (expires !== null && expires <= nowMs) return false;
  return true;
}

async function evaluatePermission(policy, actionKey, identity) {
  const nowMs = Date.now();
  const grants = (policy?.permissions?.grants || [])
    .map(normalizeGrant)
    .filter((grant) => grant.actionKey === actionKey)
    .filter((grant) => matchesSubject(grant, identity))
    .filter((grant) => isGrantActive(grant, nowMs))
    .sort((a, b) => {
      if (b.priority !== a.priority) return b.priority - a.priority;
      const specDiff = subjectSpecificity(b.subjectType) - subjectSpecificity(a.subjectType);
      if (specDiff !== 0) return specDiff;
      return (parsedTime(b.createdAtUtc) ?? 0) - (parsedTime(a.createdAtUtc) ?? 0);
    });

  const selected = grants[0];
  if (selected) {
    return {
      allowed: selected.allowed,
      reasonCode: selected.source === "TemporaryGrant" ? "TemporaryPermissionActive" : "PermissionGrantMatched",
      grantId: selected.grantId,
      expiresAtUtc: selected.expiresAtUtc
    };
  }

  const allowed = policy?.permissions?.defaultPermissions?.[actionKey] === true;
  return {
    allowed,
    reasonCode: allowed ? "GlobalDefaultAllow" : "GlobalDefaultDeny",
    grantId: null,
    expiresAtUtc: null
  };
}

async function getDownloadDecision() {
  const [identityResponse, policyResponse] = await Promise.all([
    sendNative({ type: "getIdentity" }),
    sendNative({ type: "getPolicy" })
  ]);

  if (!policyResponse?.success || !policyResponse.data) {
    return { allowed: false, reasonCode: "PolicyUnavailable", grantId: null };
  }

  const identity = identityResponse?.success ? identityResponse : {};
  return await evaluatePermission(policyResponse.data, DOWNLOAD_ACTION, identity);
}

function auditDownload(downloadItem, result, decision) {
  const details = JSON.stringify({
    file: downloadItem.filename || downloadItem.url || "download",
    permissionGrantId: decision?.grantId ?? null,
    permissionExpiresAtUtc: decision?.expiresAtUtc ?? null
  });

  return sendNative({
    type: "audit",
    eventType: "download",
    action: DOWNLOAD_ACTION,
    result,
    reasonCode: decision?.reasonCode || "GlobalDefaultDeny",
    ruleId: decision?.grantId || "",
    details,
    destination: downloadItem.finalUrl || downloadItem.url || "",
    resourceName: downloadItem.filename || ""
  });
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "getContext") {
    Promise.all([
      sendNative({ type: "getIdentity" }),
      sendNative({ type: "getPolicy" })
    ]).then(([identity, policyResponse]) => {
      sendResponse({
        identity: identity?.success ? identity : null,
        policy: policyResponse?.success ? policyResponse.data : null,
        nativeError: policyResponse?.success ? null : policyResponse?.message
      });
    });
    return true;
  }

  if (message?.type === "classifyText" || message?.type === "classifyFile" || message?.type === "audit") {
    sendNative(message).then(sendResponse);
    return true;
  }

  // Transmission-time check for browser.upload/browser.drag-drop (see page-guard.js): a fast local
  // cache+grant lookup on the agent, keyed by the file's hash - never a live AI classification call.
  // Routed straight to the native evaluatePermission message rather than the locally-cached
  // evaluatePermission() function above, since only the agent has the local file-classification
  // cache needed to resolve a ClassificationTier-scoped grant.
  if (message?.type === "evaluateFileUpload") {
    sendNative({ type: "evaluatePermission", actionKey: message.actionKey, fileHash: message.fileHash })
      .then((response) => {
        sendResponse({
          allowed: response?.success === true && response?.data?.isAllowed === true,
          reasonCode: response?.data?.reasonCode || (response?.success ? "" : "NativeRequestFailed"),
          fileClassification: response?.data?.fileClassification || "",
          fileClassificationReasonCode: response?.data?.fileClassificationReasonCode || ""
        });
      });
    return true;
  }

  return false;
});

function invokeDownloadApi(method, ...args) {
  return new Promise((resolve) => {
    try {
      chrome.downloads[method](...args, () => {
        const error = chrome.runtime.lastError?.message ?? null;
        resolve({ success: !error, error });
      });
    } catch (error) {
      resolve({ success: false, error: String(error) });
    }
  });
}

async function stopAndRemoveDownload(downloadId) {
  await invokeDownloadApi("cancel", downloadId);
  // removeFile also handles the race where a very small download completed
  // before the asynchronous permission decision returned.
  await invokeDownloadApi("removeFile", downloadId);
  await new Promise((resolve) => {
    try {
      chrome.downloads.erase({ id: downloadId }, () => {
        void chrome.runtime.lastError;
        resolve();
      });
    } catch (_) {
      resolve();
    }
  });
}

function showDownloadBlockedAlert(downloadItem) {
  const showSystemNotification = () => {
    try {
      chrome.notifications.create(
        `company-dlp-download-${downloadItem.id}-${Date.now()}`,
        {
          type: "basic",
          iconUrl: "icon128.png",
          title: "Company DLP",
          message: "Download blocked by company security policy."
        },
        () => void chrome.runtime.lastError
      );
    } catch (_) { }
  };

  if (!Number.isInteger(downloadItem.tabId) || downloadItem.tabId < 0) {
    showSystemNotification();
    return;
  }

  chrome.tabs.sendMessage(
    downloadItem.tabId,
    {
      type: "showDlpAlert",
      title: "Download blocked",
      message: "Downloading files through this browser is not allowed by company security policy."
    },
    () => {
      if (chrome.runtime.lastError) {
        showSystemNotification();
      }
    }
  );
}

function showDownloadBlockedPage(downloadItem) {
  try {
    chrome.tabs.create(
      {
        url: chrome.runtime.getURL("download-blocked.html"),
        active: true
      },
      () => void chrome.runtime.lastError
    );
  } catch (_) { }
}
async function showNativeDownloadAlert(downloadItem) {
  const message =
    "Company DLP blocked this download because downloads are not allowed by the current security policy.";

  let tabId =
    Number.isInteger(downloadItem.tabId) && downloadItem.tabId >= 0
      ? downloadItem.tabId
      : null;

  if (tabId === null) {
    try {
      const tabs = await chrome.tabs.query({
        active: true,
        lastFocusedWindow: true
      });

      tabId = tabs?.[0]?.id ?? null;
    } catch (_) { }
  }

  if (Number.isInteger(tabId)) {
    try {
      await chrome.scripting.executeScript({
        target: { tabId },
        func: (text) => window.alert(text),
        args: [message]
      });

      return;
    } catch (_) { }
  }

  // Fallback when the page does not permit script injection.
  try {
    chrome.notifications.create(
      `company-dlp-download-${Date.now()}`,
      {
        type: "basic",
        iconUrl: "icon128.png",
        title: "Company DLP",
        message
      },
      () => void chrome.runtime.lastError
    );
  } catch (_) { }
}
function sendDlpAlertMessage(tabId, payload) {
  return new Promise((resolve) => {
    try {
      chrome.tabs.sendMessage(tabId, payload, () => {
        resolve(!chrome.runtime.lastError);
      });
    } catch (_) {
      resolve(false);
    }
  });
}

function injectCompanyDlpContentScript(tabId) {
  return new Promise((resolve) => {
    try {
      chrome.scripting.executeScript(
        {
          target: { tabId },
          files: ["content.js"]
        },
        () => resolve(!chrome.runtime.lastError)
      );
    } catch (_) {
      resolve(false);
    }
  });
}

function getActiveDownloadTabId(downloadItem) {
  if (Number.isInteger(downloadItem?.tabId) && downloadItem.tabId >= 0) {
    return Promise.resolve(downloadItem.tabId);
  }

  return new Promise((resolve) => {
    try {
      chrome.tabs.query(
        {
          active: true,
          lastFocusedWindow: true
        },
        (tabs) => {
          if (chrome.runtime.lastError) {
            resolve(null);
            return;
          }

          const tabId = tabs?.[0]?.id;
          resolve(Number.isInteger(tabId) ? tabId : null);
        }
      );
    } catch (_) {
      resolve(null);
    }
  });
}

async function showStandardDlpDownloadAlert(downloadItem) {
  const tabId = await getActiveDownloadTabId(downloadItem);
  if (!Number.isInteger(tabId)) return false;

  const payload = {
    type: "showDlpAlert",
    title: "Download blocked",
    message: "Downloading files through this browser is not allowed by company security policy."
  };

  if (await sendDlpAlertMessage(tabId, payload)) {
    return true;
  }

  // The tab may have been open before the unpacked extension was reloaded.
  // Inject the existing Company DLP content script, then retry the same alert.
  if (!await injectCompanyDlpContentScript(tabId)) {
    return false;
  }

  return await sendDlpAlertMessage(tabId, payload);
}
async function showExactCompanyDlpDownloadPopup(downloadItem) {
  let tabId =
    Number.isInteger(downloadItem?.tabId) && downloadItem.tabId >= 0
      ? downloadItem.tabId
      : null;

  if (tabId === null) {
    try {
      const tabs = await chrome.tabs.query({
        active: true,
        lastFocusedWindow: true
      });

      tabId = tabs?.[0]?.id ?? null;
    } catch (_) { }
  }

  if (!Number.isInteger(tabId)) return false;

  try {
    await chrome.scripting.executeScript({
      target: { tabId },
      func: (title, message) => {
        const old = document.getElementById("company-dlp-v2-notice");
        if (old) old.remove();

        const notice = document.createElement("div");
        notice.id = "company-dlp-v2-notice";
        notice.setAttribute("role", "alert");
        notice.setAttribute("aria-live", "assertive");

        const heading = document.createElement("div");
        heading.textContent = title;
        Object.assign(heading.style, {
          font: "700 17px Arial,sans-serif",
          marginBottom: "6px"
        });

        const body = document.createElement("div");
        body.textContent = message;
        Object.assign(body.style, {
          font: "500 14px/1.45 Arial,sans-serif"
        });

        const footer = document.createElement("div");
        footer.textContent =
          "Company DLP • Action blocked by company security policy";

        Object.assign(footer.style, {
          font: "500 11px Arial,sans-serif",
          opacity: ".82",
          marginTop: "8px"
        });

        notice.append(heading, body, footer);

        Object.assign(notice.style, {
          position: "fixed",
          zIndex: "2147483647",
          top: "18px",
          right: "18px",
          width: "min(430px, calc(100vw - 36px))",
          boxSizing: "border-box",
          background: "#b91c1c",
          color: "white",
          padding: "16px 18px",
          border: "1px solid rgba(255,255,255,.55)",
          borderRadius: "12px",
          boxShadow: "0 16px 40px rgba(0,0,0,.45)",
          pointerEvents: "none",
          transform: "translateY(-12px)",
          opacity: "0",
          transition: "transform .18s ease, opacity .18s ease"
        });

        (document.documentElement || document.body).appendChild(notice);

        requestAnimationFrame(() => {
          notice.style.transform = "translateY(0)";
          notice.style.opacity = "1";
        });

        setTimeout(() => {
          notice.style.transform = "translateY(-12px)";
          notice.style.opacity = "0";

          setTimeout(() => notice.remove(), 220);
        }, 6000);
      },
      args: [
        "Download blocked",
        "Downloading files through this browser is not allowed by company security policy."
      ]
    });

    return true;
  } catch (_) {
    return false;
  }
}
async function handleCreatedDownload(downloadItem) {
  // Pause immediately before calling the Service. This prevents fast data/blob
  // downloads from finishing while the permission decision is being evaluated.
  const pauseResult = await invokeDownloadApi("pause", downloadItem.id);

  let decision;
  try {
    decision = await getDownloadDecision();
  } catch (_) {
    decision = {
      allowed: false,
      reasonCode: "PermissionEvaluationFailed",
      grantId: null
    };
  }

  if (decision.allowed) {
    if (pauseResult.success) {
      await invokeDownloadApi("resume", downloadItem.id);
    }
    await auditDownload(downloadItem, "allowed", decision);
    return;
  }

  await stopAndRemoveDownload(downloadItem.id);

  // Display the exact same Company DLP popup used by the other browser actions.
  void showExactCompanyDlpDownloadPopup(downloadItem);

  // Audit runs in the background and never delays the popup.
  void auditDownload(downloadItem, "blocked", decision);
}
chrome.downloads.onCreated.addListener((downloadItem) => {
  void handleCreatedDownload(downloadItem);
});
