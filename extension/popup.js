import { DEFAULTS, TROUBLESHOOT, filterLinks, isVideoSite, qualityLabel, siteOf } from "./helpers.js";

const $ = (id) => document.getElementById(id);
const ask = (msg) => chrome.runtime.sendMessage(msg);
const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });

// Settings
const s = { ...DEFAULTS, ...(await chrome.storage.sync.get(Object.keys(DEFAULTS))) };
$("enabled").checked = s.enabled;
$("video").checked = s.videoButton;
$("min").value = s.minSizeMB;
$("skip").value = s.skipTypes;
$("enabled").onchange = () => chrome.storage.sync.set({ enabled: $("enabled").checked });
$("video").onchange = () => chrome.storage.sync.set({ videoButton: $("video").checked });
$("min").onchange = () => chrome.storage.sync.set({ minSizeMB: Math.max(0, Number($("min").value) || 0) });
$("skip").onchange = () => chrome.storage.sync.set({ skipTypes: $("skip").value });
$("sites").value = s.skipSites;
$("sites").onchange = () => chrome.storage.sync.set({ skipSites: $("sites").value });

// Keep downloads in the browser for a while, or always for this site.
function showPause(until) {
  const paused = until > Date.now();
  $("pause").textContent = paused ? "Resume taking over" : "Pause for 30 minutes";
  $("paused").hidden = !paused;
  if (paused) $("paused").textContent = `Downloads stay in the browser until ${new Date(until).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}.`;
}
showPause(s.pausedUntil);
$("pause").onclick = async () => {
  const { pausedUntil = 0 } = await chrome.storage.sync.get("pausedUntil");
  const until = pausedUntil > Date.now() ? 0 : Date.now() + 30 * 60 * 1000;
  await chrome.storage.sync.set({ pausedUntil: until });
  showPause(until);
};
const site = tab && /^https?:/i.test(tab.url || "") ? siteOf(tab.url) : "";
$("thisSite").hidden = !site;
if (site) $("thisSite").textContent = `Keep downloads from ${site} in the browser`;
$("thisSite").onclick = async () => {
  const list = $("sites").value.split(/[\s,]+/).filter(Boolean);
  if (!list.map(siteOf).includes(site)) list.push(site);
  $("sites").value = list.join(", ");
  await chrome.storage.sync.set({ skipSites: $("sites").value });
};

// Site access (Firefox asks for it separately)
const all = { origins: ["<all_urls>"] };
if (chrome.permissions && !(await chrome.permissions.contains(all))) {
  $("access").hidden = false;
  $("grant").onclick = () => chrome.permissions.request(all).then((ok) => ($("access").hidden = ok));
}

// One link
$("send").onclick = async () => {
  const url = $("link").value.trim();
  if (!/^https?:\/\//i.test(url)) return;
  const r = await ask({ type: "send-link", url, referrer: tab && tab.url });
  $("link").value = r && r.ok ? "" : url;
};

function item(url, label, extra) {
  const li = document.createElement("li");
  const span = document.createElement("span");
  span.textContent = label;
  span.title = url;
  li.append(extra, span);
  return li;
}

// YouTube, Instagram, Facebook, X … : the app saves the page's video in the best quality
if (tab && isVideoSite(tab.url)) {
  $("siteBox").hidden = false;
  const go = (audioOnly, b) => async () => {
    const r = await ask({ type: "send-link", url: tab.url, referrer: tab.url, site: true, audioOnly });
    b.textContent = r && r.ok ? "Sent" : "Failed";
  };
  $("siteVideo").onclick = go(false, $("siteVideo"));
  $("siteAudio").onclick = go(true, $("siteAudio"));
}

// Media seen on this tab
if (tab) {
  const { media = [] } = (await ask({ type: "media-list", tabId: tab.id })) || {};
  $("noMedia").hidden = media.length > 0;
  for (const m of media) {
    const b = document.createElement("button");
    b.textContent = m.protected ? "Protected" : "Download";
    b.disabled = !!m.protected;
    b.title = m.protected ? "The site protects this video, so it can't be saved" : "Best quality";
    b.onclick = async () => {
      const msg = { type: "send-link", url: m.url, referrer: tab.url };
      if (m.kind === "stream") Object.assign(msg, { stream: true, fileName: m.name });
      const r = await ask(msg);
      b.textContent = r && r.ok ? "Sent" : "Failed";
    };
    const li = item(m.url, `${m.name} · ${qualityLabel(m)}${m.kind === "stream" ? " · format detected after download" : ""}`, document.createTextNode(""));
    li.append(b);
    $("media").append(li);
  }
}

// Links on this page
let found = [];
$("find").onclick = async () => {
  if (!tab) return;
  const { links = [] } = (await ask({ type: "page-links", tabId: tab.id })) || {};
  found = filterLinks(links, $("types").value);
  $("links").textContent = "";
  for (const url of found) {
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = true;
    box.value = url;
    $("links").append(item(url, decodeURI(url.split("/").pop() || url), box));
  }
  $("links").hidden = $("linkActions").hidden = found.length === 0;
  if (!found.length) $("find").textContent = "None found";
};
const boxes = () => [...$("links").querySelectorAll("input")];
$("all").onclick = () => boxes().forEach((b) => (b.checked = true));
$("none").onclick = () => boxes().forEach((b) => (b.checked = false));
async function sendChecked(later) {
  const urls = boxes().filter((b) => b.checked).map((b) => b.value);
  if (!urls.length) return;
  const r = await ask({ type: "send-links", urls, later, referrer: tab && tab.url });
  $("download").textContent = r && r.ok ? `Sent ${r.sent}` : "Failed";
}
$("download").onclick = () => sendChecked(false);
$("later").onclick = () => sendChecked(true);

// App status
const r = await ask({ type: "ping" });
$("status").textContent = r && r.ok ? `Connected to the app (${r.version})` : TROUBLESHOOT;
$("status").className = r && r.ok ? "ok" : "bad";
