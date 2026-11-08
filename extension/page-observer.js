// Isolated, non-invasive URL observation inside the page MAIN world.
// Does not read video payloads or override the browser's media pipeline.
(() => {
  if (window.__meteorPageObserver) return;
  Object.defineProperty(window, "__meteorPageObserver", { value: true });
  const candidate = (url, mime = "") => {
    if (typeof url !== "string" || url.length > 8192) return;
    const type = String(mime || "").toLowerCase();
    if (!/\.m3u8(?:[?#]|$)|\.mpd(?:[?#]|$)|\.(?:mp4|m4a|webm)(?:[?#]|$)/i.test(url) &&
        !/mpegurl|dash\+xml|^(video|audio)\//i.test(type)) return;
    try {
      const u = new URL(url, location.href);
      if (!/^https?:$/.test(u.protocol)) return;
      window.postMessage({ source: "meteor-page-observer", type: "media", url: u.href, mime: type }, location.origin);
    } catch {}
  };
  const originalFetch = window.fetch;
  if (typeof originalFetch === "function") {
    window.fetch = function (...args) {
      const address = typeof args[0] === "string" ? args[0] : args[0]?.url;
      return originalFetch.apply(this, args).then((response) => {
        try { candidate(response.url || address, response.headers.get("content-type")); } catch {}
        return response;
      });
    };
  }
  const open = XMLHttpRequest.prototype.open;
  const send = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url, ...rest) {
    this.__meteorUrl = typeof url === "string" ? url : String(url);
    return open.call(this, method, url, ...rest);
  };
  XMLHttpRequest.prototype.send = function (...args) {
    this.addEventListener("load", () => {
      try { candidate(this.responseURL || this.__meteorUrl, this.getResponseHeader("content-type")); } catch {}
    }, { once: true });
    return send.apply(this, args);
  };
})();
