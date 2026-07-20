const api = globalThis.browser || globalThis.chrome;
const NATIVE_HOST = "com.company.dlp";

async function sendNative(message) {
  try {
    return await api.runtime.sendNativeMessage(NATIVE_HOST, message);
  } catch (error) {
    return { success: false, message: String(error?.message || error) };
  }
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

api.downloads.onCreated.addListener(async (downloadItem) => {
  const policyResponse = await sendNative({ type: "getPolicy" });
  const policy = policyResponse?.success ? policyResponse.data : null;
  if (policy?.browser?.blockDownloads === false) return;

  try { await api.downloads.cancel(downloadItem.id); } catch (_) { }
  const details = downloadItem.filename || downloadItem.url || "download";
  await sendNative({
    type: "audit",
    eventType: "browser",
    action: "download",
    result: "blocked",
    details,
    destination: downloadItem.finalUrl || downloadItem.url || ""
  });

  if (policy?.notifications?.showBrowserPageAlerts !== false && Number.isInteger(downloadItem.tabId) && downloadItem.tabId >= 0) {
    try {
      await api.tabs.sendMessage(downloadItem.tabId, {
        type: "showDlpAlert",
        title: "Download blocked",
        message: "Downloading files through this browser is not allowed by company security policy."
      });
    } catch (_) { }
  }
});
