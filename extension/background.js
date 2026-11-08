// Meteor browser integration — independent Manifest V3 implementation.
// The Chrome IDM extension is a functional reference, not a runtime dependency.
import {
  HOST, DEFAULTS, TROUBLESHOOT, addMedia, cookieHeader, header, isMedia,
  isStreamInitialization, isStreamPlaylist, isVideoSite, isWebLink, mayBeMedia, nameFromUrl,
  originOf, shouldTakeOver, sortMedia, summarizePlaylist, titleToName, downloadKey,
} from "./helpers.js";

const MAX_ITEMS = 30;
const requestDetails = new Map();
const handled = new Map();
const inspecting = new Map();
let storageQueue = Promise.resolve();
const serial = (fn) => (storageQueue = storageQueue.then(fn, fn));
const config = async () => ({ ...DEFAULTS, ...(await chrome.storage.sync.get(Object.keys(DEFAULTS))) });
const getMedia = async (id) => (await chrome.storage.session.get("media-" + id))["media-" + id] || [];
const setMedia = async (id, list) => chrome.storage.session.set({ ["media-" + id]: list.slice(0, MAX_ITEMS) });
const validTab = (id) => Number.isInteger(id) && id >= 0;
const getHeader = (headers, name) => header(headers || [], name);
const getOrigin = (d) => d.initiator || d.documentUrl || d.originUrl || "";

async function showStatus(ok) {
  await chrome.action.setBadgeText({ text: ok ? "" : "!" }).catch(() => {});
  if (!ok) await chrome.action.setBadgeBackgroundColor({ color: "#cf222e" }).catch(() => {});
  await chrome.action.setTitle({ title: ok ? "Meteor Download Manager" : TROUBLESHOOT }).catch(() => {});
}
async function cookiesFor(url) {
  try { return cookieHeader(await chrome.cookies.getAll({ url })); }
  catch { return ""; }
}
async function send(url, extra = {}) {
  if (!isWebLink(url)) return false;
  const message = {
    type: "download", url, cookies: await cookiesFor(url),
    userAgent: navigator.userAgent, ...extra,
  };
  try {
    const r = await Promise.race([
      chrome.runtime.sendNativeMessage(HOST, message),
      new Promise((resolve) => setTimeout(() => resolve(null), 8000)),
    ]);
    const ok = !!r?.ok;
    await showStatus(ok);
    return ok;
  } catch { await showStatus(false); return false; }
}
async function findTab(d) {
  if (validTab(d.tabId)) return d.tabId;
  const origin = originOf(getOrigin(d));
  if (!origin) return -1;
  try {
    const tabs = (await chrome.tabs.query({})).filter((t) => originOf(t.url || "") === origin);
    return (tabs.find((t) => t.active) || (tabs.length === 1 ? tabs[0] : null))?.id ?? -1;
  } catch { return -1; }
}
async function offer(tabId, item) {
  if (!validTab(tabId) || !isWebLink(item.url)) return;
  return serial(async () => {
    const old = await getMedia(tabId);
    const list = addMedia(old, item);
    await setMedia(tabId, sortMedia(list));
  });
}
function rememberRequest(d) {
  if (!isWebLink(d.url)) return;
  const h = d.requestHeaders || [];
  requestDetails.set(d.requestId, {
    url: d.url, tabId: d.tabId, referrer: getHeader(h, "referer") || getOrigin(d),
    origin: getHeader(h, "origin"), time: Date.now(),
  });
  if (requestDetails.size > 2000) {
    const cutoff = Date.now() - 120000;
    for (const [key, v] of requestDetails) if (v.time < cutoff) requestDetails.delete(key);
  }
}
chrome.webRequest.onBeforeSendHeaders.addListener(
  rememberRequest,
  { urls: ["<all_urls>"], types: ["media", "xmlhttprequest", "other", "object"] },
  ["requestHeaders", "extraHeaders"],
);
chrome.webRequest.onErrorOccurred.addListener((d) => requestDetails.delete(d.requestId), { urls: ["<all_urls>"] });
chrome.webRequest.onCompleted.addListener((d) => requestDetails.delete(d.requestId), { urls: ["<all_urls>"] });

async function inspectPlaylist(tabId, url, context) {
  const key = tabId + "|" + url;
  if (inspecting.has(key)) return;
  inspecting.set(key, Date.now());
  if (inspecting.size > 300) {
    for (const [k, t] of inspecting) if (Date.now() - t > 180000) inspecting.delete(k);
  }
  let summary = null;
  try {
    // Some playlists cannot be fetched by the extension with the page's request context.
    // Offer confirmed HLS responses even if this optional inspection fails.
    const response = await fetch(url, { credentials: "include", redirect: "follow" });
    if (response.ok) {
      const body = await response.text();
      const result = summarizePlaylist(body, url);
      if (result.valid) summary = result;
    }
  } catch { /* native engine performs final validation */ }
  if (!summary) summary = {
    variants: [], height: 0, protected: false, live: false, unverified: true,
  };
  let title = "video";
  try { title = titleToName((await chrome.tabs.get(tabId)).title); } catch {}
  const list = await getMedia(tabId);
  if (list.some((m) => (m.variants || []).includes(url))) return;
  await offer(tabId, {
    kind: "stream", url, name: title, height: summary.height, size: -1,
    variants: summary.variants, protected: summary.protected, live: summary.live,
    unverified: !!summary.unverified, referrer: context.referrer || "",
  });
}
async function onResponse(d) {
  if (d.statusCode >= 400 || !isWebLink(d.url)) return;
  const mime = getHeader(d.responseHeaders, "content-type");
  if (!mayBeMedia(d.url, mime)) return;
  const tabId = await findTab(d);
  if (!validTab(tabId)) return;
  if (isVideoSite(getOrigin(d))) return; // site's own resolver handles those pages
  const context = requestDetails.get(d.requestId) || {};
  if (isStreamPlaylist(d.url, mime)) {
    await inspectPlaylist(tabId, d.url, context);
    return;
  }
  const range = getHeader(d.responseHeaders, "content-range");
  const length = Number(range.match(/\/(\d+)\s*$/)?.[1] || getHeader(d.responseHeaders, "content-length") || -1);
  if (isStreamInitialization(d.url)) return; // An HLS init fragment is not a standalone media download.
  if (isMedia(d.url, mime, length)) await offer(tabId, {
    kind: "file", url: d.url, name: /^audio\//i.test(mime) && /\.mp4$/i.test(nameFromUrl(d.url))
      ? nameFromUrl(d.url).replace(/\.mp4$/i, "") : nameFromUrl(d.url),
    size: length > 0 ? length : -1, type: mime.split(";")[0], referrer: context.referrer || "",
  });
}
chrome.webRequest.onHeadersReceived.addListener(
  (d) => { onResponse(d).catch(() => {}); },
  { urls: ["<all_urls>"], types: ["media", "object", "xmlhttprequest", "other"] },
  ["responseHeaders"],
);

// Browser download takeover: only cancel browser downloads after native acceptance.
chrome.downloads.onCreated.addListener(async (item) => {
  if (!shouldTakeOver(item, await config())) return;
  try { await chrome.downloads.pause(item.id); } catch { return; }
  const url = item.finalUrl || item.url;
  const key = downloadKey(url);
  const now = Date.now();
  for (const [k, t] of handled) if (now - t > 10000) handled.delete(k);
  if (handled.has(key)) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
    return;
  }
  handled.set(key, now);
  const ok = await send(url, {
    fileName: /^audio\//i.test(item.mime || "") && /\.mp4$/i.test(item.filename || "")
      ? item.filename.split(/[\\/]/).pop().replace(/\.mp4$/i, "")
      : item.filename?.split(/[\\/]/).pop(),
    referrer: item.referrer || "", size: item.totalBytes > 0 ? item.totalBytes : undefined,
    browser: true,
  });
  if (ok) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  } else {
    handled.delete(key);
    await chrome.downloads.resume(item.id).catch(() => {});
  }
});

function setupMenus() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: "meteor-link", title: "Download with Meteor", contexts: ["link", "video", "audio"] });
    chrome.contextMenus.create({ id: "meteor-media", title: "Download this page's video with Meteor", contexts: ["page", "video"] });
    chrome.contextMenus.create({ id: "meteor-all", title: "Download page links with Meteor", contexts: ["page", "selection"] });
  });
}
chrome.runtime.onInstalled.addListener(setupMenus);
async function pageLinks(tabId, selected = false) {
  const results = await chrome.scripting.executeScript({
    target: { tabId }, args: [selected],
    func: (onlySelected) => {
      const selection = window.getSelection();
      return [...document.querySelectorAll("a[href]")].filter((a) =>
        !onlySelected || (selection?.rangeCount && selection.containsNode(a, true))
      ).map((a) => a.href);
    },
  });
  return results[0]?.result || [];
}
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const referrer = info.pageUrl || tab?.url || "";
  if (info.menuItemId === "meteor-all" && tab?.id !== undefined) {
    for (const url of await pageLinks(tab.id, !!info.selectionText)) await send(url, { referrer });
  } else if (info.menuItemId === "meteor-media") {
    await send(referrer, { referrer, site: true, browser: true });
  } else if (info.menuItemId === "meteor-link") {
    await send(info.linkUrl || info.srcUrl, { referrer, browser: true });
  }
});
chrome.tabs.onUpdated.addListener((id, change) => {
  if (change.status === "loading" && change.url) {
    for (const key of inspecting.keys()) if (key.startsWith(id + "|")) inspecting.delete(key);
    serial(() => setMedia(id, [])).catch(() => {});
  }
});
chrome.tabs.onRemoved.addListener((id) => {
  chrome.storage.session.remove("media-" + id).catch(() => {});
  for (const key of inspecting.keys()) if (key.startsWith(id + "|")) inspecting.delete(key);
});

chrome.runtime.onMessage.addListener((msg, sender, respond) => {
  const run = async () => {
    const tabId = sender.tab?.id ?? msg.tabId;
    const referrer = msg.referrer || sender.tab?.url || "";
    switch (msg.type) {
      case "detected-media": {
        // A page can only report URLs for its own tab; never trust caller-supplied tab IDs.
        if (!sender.tab || !validTab(tabId) || !isWebLink(msg.url)) return { ok: false };
        if (isStreamPlaylist(msg.url, msg.mime || "")) {
          await inspectPlaylist(tabId, msg.url, { referrer });
        } else if (!isStreamInitialization(msg.url) && isMedia(msg.url, msg.mime || "", -1)) {
          await offer(tabId, { kind: "file", url: msg.url, name: nameFromUrl(msg.url), size: -1, referrer });
        }
        return { ok: true };
      }
      case "send-link": {
        const extra = { referrer, later: !!msg.later, browser: true };
        if (msg.stream) Object.assign(extra, { stream: true, fileName: msg.fileName });
        else if (msg.site) {
          Object.assign(extra, { site: true, audioOnly: !!msg.audioOnly });
          if (Number.isInteger(msg.maxHeight) && msg.maxHeight >= 100 && !msg.audioOnly) extra.maxHeight = msg.maxHeight;
        } else if (msg.fileName) extra.fileName = msg.fileName;
        return { ok: await send(msg.url, extra) };
      }
      case "send-links": {
        let sent = 0;
        for (const url of (msg.urls || []).slice(0, 200)) if (await send(url, { referrer, later: !!msg.later })) sent++;
        return { ok: sent > 0, sent };
      }
      case "media-list": return { media: validTab(tabId) ? await getMedia(tabId) : [] };
      case "page-links": return { links: validTab(tabId) ? await pageLinks(tabId) : [] };
      case "video-button": {
        const media = validTab(tabId) ? await getMedia(tabId) : [];
        const found = media.filter((m) => !m.protected);
        if (isWebLink(msg.src) && !found.some((m) => m.url === msg.src))
          found.push({ url: msg.src, name: nameFromUrl(msg.src), size: -1 });
        return { media: sortMedia(found), locked: media.some((m) => m.protected) };
      }
      case "ping": {
        const reply = await chrome.runtime.sendNativeMessage(HOST, { type: "ping" }).catch(() => null);
        await showStatus(!!reply?.ok);
        return { ok: !!reply?.ok, version: reply?.version };
      }
      default: return { ok: false };
    }
  };
  run().then(respond, () => respond({ ok: false }));
  return true;
});
