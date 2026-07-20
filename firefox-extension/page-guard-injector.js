(() => {
  try {
    const runtime = globalThis.browser?.runtime || globalThis.chrome?.runtime;
    if (!runtime?.getURL) return;
    const script = document.createElement("script");
    script.src = runtime.getURL("page-guard.js");
    script.async = false;
    script.onload = () => script.remove();
    (document.documentElement || document.head || document).appendChild(script);
  } catch (_) { }
})();
