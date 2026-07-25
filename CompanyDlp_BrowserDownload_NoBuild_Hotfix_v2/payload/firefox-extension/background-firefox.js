const api = globalThis.browser || globalThis.chrome;
const NATIVE_HOST = "com.company.dlp";
const DOWNLOAD_ACTION = "browser.download";

async function sendNative(message) {
  try {
    return await api.runtime.sendNativeMessage(NATIVE_HOST, message);
  } catch (error) {
    return { success: false, message: String(error?.message || error) };
  }
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

api.runtime.onMessage.addListener((message) => {
  if (message?.type === "getContext") {
    return Promise.all([
      sendNative({ type: "getIdentity" }),
      sendNative({ type: "getPolicy" })
    ]).then(([identity, policyResponse]) => ({
      identity: identity?.success ? identity : null,
      policy: policyResponse?.success ? policyResponse.data : null,
      nativeError: policyResponse?.success ? null : policyResponse?.message
    }));
  }

  if (message?.type === "classifyText" || message?.type === "classifyFile" || message?.type === "audit") {
    return sendNative(message);
  }

  return false;
});

async function stopAndRemoveDownload(downloadId) {
  try { await api.downloads.cancel(downloadId); } catch (_) { }
  try { await api.downloads.removeFile(downloadId); } catch (_) { }
  try { await api.downloads.erase({ id: downloadId }); } catch (_) { }
}

async function showDownloadBlockedAlert(downloadItem) {
  if (Number.isInteger(downloadItem.tabId) && downloadItem.tabId >= 0) {
    try {
      await api.tabs.sendMessage(downloadItem.tabId, {
        type: "showDlpAlert",
        title: "Download blocked",
        message: "Downloading files through this browser is not allowed by company security policy."
      });
      return;
    } catch (_) { }
  }

  try {
    await api.notifications.create(
      `company-dlp-download-${downloadItem.id}-${Date.now()}`,
      {
        type: "basic",
        iconUrl: api.runtime.getURL("icon128.png"),
        title: "Company DLP",
        message: "Download blocked by company security policy."
      }
    );
  } catch (_) { }
}

api.downloads.onCreated.addListener(async (downloadItem) => {
  // Pause immediately so fast downloads cannot finish while the native
  // permission decision is being evaluated.
  let paused = false;
  try {
    await api.downloads.pause(downloadItem.id);
    paused = true;
  } catch (_) { }

  let decision;
  try {
    decision = await getDownloadDecision();
  } catch (_) {
    decision = { allowed: false, reasonCode: "PermissionEvaluationFailed", grantId: null };
  }

  if (decision.allowed) {
    if (paused) {
      try { await api.downloads.resume(downloadItem.id); } catch (_) { }
    }
    await auditDownload(downloadItem, "allowed", decision);
    return;
  }

  await stopAndRemoveDownload(downloadItem.id);
  await auditDownload(downloadItem, "blocked", decision);
  await showDownloadBlockedAlert(downloadItem);
});
