// Meteor content integration: UI and page network observer, runs in every frame.
(() => {
  if (window.__meteorContent) return;
  window.__meteorContent = true;
  let enabled = true, player = null, dismiss = 0;
  chrome.storage.sync.get({ videoButton: true }, (s) => { enabled = !!s.videoButton; });
  chrome.storage.onChanged.addListener((c) => {
    if (c.videoButton) enabled = !!c.videoButton.newValue;
  });

  // The page observer runs in MAIN world and only reports candidate URL and MIME
  // metadata. It never reads or posts response bodies, credentials or auth headers.
  try {
    const script = document.createElement("script");
    script.src = chrome.runtime.getURL("page-observer.js");
    script.async = false;
    (document.documentElement || document.head).appendChild(script);
    script.remove();
  } catch {}
  const reported = new Map();
  window.addEventListener("message", (event) => {
    if (event.source !== window || event.origin !== location.origin) return;
    const data = event.data;
    if (!data || data.source !== "meteor-page-observer" || data.type !== "media") return;
    try {
      const u = new URL(data.url, location.href);
      if (!/^https?:$/.test(u.protocol)) return;
      if (reported.has(u.href) && Date.now() - reported.get(u.href) < 30000) return;
      reported.set(u.href, Date.now());
      if (reported.size > 100) reported.delete(reported.keys().next().value);
      chrome.runtime.sendMessage({ type: "detected-media", url: u.href, mime: String(data.mime || "").slice(0, 100) }).catch(() => {});
    } catch {}
  });

  const host = document.createElement("div");
  host.style.cssText = "position:fixed;z-index:2147483647;display:none;top:0;left:0";
  const shadow = host.attachShadow({ mode: "closed" });
  shadow.innerHTML = '<style>button{cursor:pointer;font:600 12px system-ui;color:#fff;background:#1364a3;border:0;border-radius:7px;padding:7px 11px;box-shadow:0 2px 8px #0005}ul{background:#fff;color:#222;border-radius:7px;padding:5px;margin:4px 0 0;max-width:400px;max-height:300px;overflow:auto;box-shadow:0 2px 8px #0005;list-style:none;font:12px system-ui}li{padding:7px;cursor:pointer}li:hover{background:#e8f2fa}.note{cursor:default;color:#555}</style><button type="button">Download this video</button><ul hidden></ul>';
  const button = shadow.querySelector("button"), menu = shadow.querySelector("ul");
  function hide() { host.style.display = "none"; menu.hidden = true; player = null; }
  function place() {
    if (!player || !player.isConnected) return hide();
    const box = player.getBoundingClientRect();
    if (box.width < 120 || box.height < 50 || box.bottom < 0 || box.top > innerHeight) return hide();
    host.style.top = Math.max(3, box.top + 6) + "px";
    host.style.left = Math.max(3, Math.min(innerWidth - 190, box.right - 185)) + "px";
    host.style.display = "block";
  }
  function show(el) {
    if (!enabled) return;
    clearTimeout(dismiss);
    player = el;
    if (!host.isConnected) document.documentElement?.appendChild(host);
    button.textContent = el.tagName === "AUDIO" ? "Download this audio" : "Download this video";
    place();
    const src = el.currentSrc || el.src;
    if (/^https?:/i.test(src || "")) chrome.runtime.sendMessage({ type: "detected-media", url: src }).catch(() => {});
  }
  async function send(media) {
    const reply = await chrome.runtime.sendMessage({
      type: "send-link", url: media.url, referrer: location.href,
      stream: media.kind === "stream", fileName: media.name,
      site: media.kind === "site", audioOnly: !!media.audioOnly,
    }).catch(() => null);
    button.textContent = reply?.ok ? "Sent to Meteor" : "Meteor is unavailable";
    dismiss = setTimeout(hide, 1400);
  }
  function entry(label, callback, note = false) {
    const li = document.createElement("li");
    li.textContent = label;
    if (note) li.className = "note";
    else li.addEventListener("click", callback);
    menu.appendChild(li);
  }
  const videoSite = /(^|\.)(youtube\.com|youtu\.be|instagram\.com|facebook\.com|x\.com|twitter\.com|tiktok\.com|vimeo\.com|dailymotion\.com|reddit\.com|twitch\.tv)$/i.test(location.hostname);
  button.addEventListener("click", async (event) => {
    event.preventDefault(); event.stopPropagation();
    menu.replaceChildren();
    if (videoSite) {
      entry("Video, best quality", () => send({ url: location.href, kind: "site" }));
      entry("Audio only", () => send({ url: location.href, kind: "site", audioOnly: true }));
      menu.hidden = false;
      return;
    }
    const src = player?.currentSrc || player?.src;
    const response = await chrome.runtime.sendMessage({ type: "video-button", src }).catch(() => null);
    const media = response?.media || [];
    if (media.length === 1) return send(media[0]);
    if (!media.length) entry(response?.locked ? "This media is protected." : "Play the video, then try again.", null, true);
    for (const m of media) entry(m.name + (m.height ? " · " + m.height + "p" : "") + (m.live ? " · live" : "") + (m.kind === "stream" ? " · format pending" : ""), () => send(m));
    menu.hidden = false;
  });
  document.addEventListener("mouseover", (event) => {
    const el = event.target?.closest?.("video,audio");
    if (el) show(el);
    else if (player && !host.contains(event.target)) {
      clearTimeout(dismiss);
      dismiss = setTimeout(hide, 900);
    }
  }, true);
  host.addEventListener("mouseenter", () => clearTimeout(dismiss));
  host.addEventListener("mouseleave", () => { dismiss = setTimeout(hide, 900); });
  addEventListener("scroll", () => { if (player) requestAnimationFrame(place); }, { passive: true, capture: true });
  addEventListener("resize", () => { if (player) requestAnimationFrame(place); });
  const observer = new MutationObserver(() => {
    if (player && !player.isConnected) hide();
  });
  observer.observe(document, { childList: true, subtree: true });
})();
