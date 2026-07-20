const NATIVE_HOST = "com.company.dlp";

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

  return false;
});

chrome.downloads.onCreated.addListener((downloadItem) => {
  sendNative({ type: "getPolicy" }).then((policyResponse) => {
    const policy = policyResponse?.success ? policyResponse.data : null;
    if (policy?.browser?.blockDownloads === false) return;

    chrome.downloads.cancel(downloadItem.id, () => {
      const details = downloadItem.filename || downloadItem.url || "download";
      sendNative({
        type: "audit",
        eventType: "browser",
        action: "download",
        result: "blocked",
        details,
        destination: downloadItem.finalUrl || downloadItem.url || ""
      });

      if (policy?.notifications?.showBrowserPageAlerts !== false
          && Number.isInteger(downloadItem.tabId)
          && downloadItem.tabId >= 0) {
        chrome.tabs.sendMessage(downloadItem.tabId, {
          type: "showDlpAlert",
          title: "Download blocked",
          message: "Downloading files through this browser is not allowed by company security policy."
        }, () => void chrome.runtime.lastError);
      }
    });
  });
});
