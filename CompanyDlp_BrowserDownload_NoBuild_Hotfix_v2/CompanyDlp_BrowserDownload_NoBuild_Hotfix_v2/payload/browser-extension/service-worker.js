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

function normalized(value) {
  return String(value ?? "").trim().toLowerCase();
}

function parsedTime(value, fallback) {
  const parsed = Date.parse(value ?? "");
  return Number.isFinite(parsed) ? parsed : fallback;
}

function subjectSpecificity(subjectType) {
  switch (normalized(subjectType)) {
    case "usersid": return 50;
    case "username": return 40;
    case "deviceid": return 30;
    case "machinename": return 20;
    case "group": return 10;
    case "department": return 5;
    default: return 0;
  }
}

function matchesSubject(grant, identity) {
  const type = normalized(grant?.subjectType);
  const expected = normalized(grant?.subjectId);
  switch (type) {
    case "global":
      return expected === "" || expected === "*";
    case "usersid":
      return expected !== "" && expected === normalized(identity?.userSid);
    case "username":
      return expected !== "" && expected === normalized(identity?.username);
    case "machinename":
      return expected !== "" && expected === normalized(identity?.machineName);
    default:
      return false;
  }
}

function evaluatePermission(policy, identity, actionKey) {
  // Fail closed if the service or policy cannot be reached.
  if (!policy) {
    return { allowed: false, reasonCode: "PolicyUnavailable", grantId: null };
  }

  const now = Date.now();
  const grants = Array.isArray(policy?.permissions?.grants)
    ? policy.permissions.grants
    : [];

  const candidates = grants
    .filter((grant) => normalized(grant?.actionKey) === normalized(actionKey))
    .filter((grant) => !grant?.revokedAtUtc)
    .filter((grant) => parsedTime(grant?.startsAtUtc, Number.NEGATIVE_INFINITY) <= now)
    .filter((grant) => !grant?.expiresAtUtc || parsedTime(grant.expiresAtUtc, Number.NEGATIVE_INFINITY) > now)
    .filter((grant) => matchesSubject(grant, identity))
    .map((grant) => ({
      grant,
      emergencyDeny: grant?.allowed === false && normalized(grant?.source) === "emergencydeny",
      priority: Number.isFinite(Number(grant?.priority)) ? Number(grant.priority) : 0,
      specificity: subjectSpecificity(grant?.subjectType),
      createdAt: parsedTime(grant?.createdAtUtc, Number.NEGATIVE_INFINITY)
    }))
    .sort((left, right) =>
      Number(right.emergencyDeny) - Number(left.emergencyDeny)
      || right.priority - left.priority
      || right.specificity - left.specificity
      || right.createdAt - left.createdAt);

  const selected = candidates[0]?.grant;
  if (selected) {
    const temporary = normalized(selected.source) === "temporarygrant";
    const emergency = selected.allowed === false && normalized(selected.source) === "emergencydeny";
    return {
      allowed: selected.allowed === true,
      reasonCode: emergency
        ? "EmergencyDeny"
        : temporary
          ? "TemporaryPermissionActive"
          : "PermissionGrantMatched",
      grantId: selected.grantId ?? null,
      expiresAtUtc: selected.expiresAtUtc ?? null
    };
  }

  const defaults = policy?.permissions?.defaultPermissions ?? {};
  const configuredKey = Object.keys(defaults)
    .find((key) => normalized(key) === normalized(actionKey));
  if (configuredKey) {
    return {
      allowed: defaults[configuredKey] === true,
      reasonCode: defaults[configuredKey] === true ? "GlobalDefaultAllow" : "GlobalDefaultDeny",
      grantId: null
    };
  }

  // Backward-compatible fallback for policies created before browser.download existed.
  const legacyAllowed = policy?.browser?.blockDownloads === false;
  return {
    allowed: legacyAllowed,
    reasonCode: legacyAllowed ? "LegacyBrowserPolicyAllow" : "LegacyBrowserPolicyDeny",
    grantId: null
  };
}

async function getDownloadDecision() {
  const [identityResponse, policyResponse] = await Promise.all([
    sendNative({ type: "getIdentity" }),
    sendNative({ type: "getPolicy" })
  ]);
  const identity = identityResponse?.success ? identityResponse : null;
  const policy = policyResponse?.success ? policyResponse.data : null;
  return evaluatePermission(policy, identity, DOWNLOAD_ACTION);
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
  await auditDownload(downloadItem, "blocked", decision);
  showDownloadBlockedAlert(downloadItem);
}

chrome.downloads.onCreated.addListener((downloadItem) => {
  void handleCreatedDownload(downloadItem);
});
