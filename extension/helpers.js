// Pure helpers shared by the background worker and the popup (tested with node --test).

export const HOST = "com.nexonic.downloadmanager";

export const DEFAULTS = { enabled: true, minSizeMB: 1, skipTypes: "", skipSites: "", pausedUntil: 0, videoButton: true };

export function extensionOf(name) {
  const clean = String(name || "").split(/[?#]/)[0];
  const base = clean.split(/[\\/]/).pop();
  const dot = base.lastIndexOf(".");
  return dot > 0 ? base.slice(dot + 1).toLowerCase() : "";
}

export function parseTypes(text) {
  return String(text || "")
    .split(/[\s,]+/)
    .map((t) => t.replace(/^\./, "").toLowerCase())
    .filter(Boolean);
}

/** Should this browser download go to the app instead? */
export function shouldTakeOver(item, settings) {
  const s = { ...DEFAULTS, ...settings };
  if (!s.enabled) return false;
  if (s.pausedUntil > (s.now ?? Date.now())) return false; // taking over is paused for a while (popup)
  const url = item.finalUrl || item.url || "";
  if (!/^https?:\/\//i.test(url)) return false; // blob:, data:, file: stay in the browser
  if (siteListed(url, s.skipSites) || siteListed(item.referrer || "", s.skipSites)) return false;
  if (item.byExtensionId) return false;
  const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize > 0 ? item.fileSize : -1;
  if (size >= 0 && size < s.minSizeMB * 1024 * 1024) return false;
  const ext = extensionOf(item.filename || url);
  if (ext && parseTypes(s.skipTypes).includes(ext)) return false;
  return true;
}

/** "example.com" (or a link) → "example.com"; "*.example.com" → "example.com". */
export function siteOf(text) {
  let h = String(text || "").trim().toLowerCase();
  try { if (h.includes("://")) h = new URL(h).hostname; } catch (_) { return ""; }
  h = h.split("/")[0].replace(/^\*\./, "").replace(/:\d+$/, "");
  return h.replace(/^\.+|\.+$/g, "");
}

/** True when the link's site (or a parent domain of it) is in the list ("example.com, files.other.org"). */
export function siteListed(url, listText) {
  let host;
  try { host = new URL(url).hostname.toLowerCase(); } catch (_) { return false; }
  return String(listText || "").split(/[\s,]+/).map(siteOf).filter(Boolean)
    .some((site) => host === site || host.endsWith("." + site));
}

/** The same download clicked twice (a redirect, a double click): same address without the #part. */
export function downloadKey(url) {
  return String(url || "").split("#")[0];
}

/** What to show when the app could not be reached. */
export const TROUBLESHOOT =
  "Nexonic Download Manager is not reachable. Open the app once so it can register with this browser, then restart the browser. " +
  "Downloads stay in the browser until then.";

/** Only plain web links are handed to the app (never file:, data:, javascript:, chrome: and the like). */
export function isWebLink(url) {
  try {
    const u = new URL(url);
    return (u.protocol === "http:" || u.protocol === "https:") && !!u.hostname;
  } catch (_) {
    return false;
  }
}

export function cookieHeader(cookies) {
  return (cookies || []).map((c) => `${c.name}=${c.value}`).join("; ");
}

export const MEDIA_TYPES = ["mp4", "m4v", "webm", "mkv", "mov", "flv", "avi", "mp3", "m4a", "aac", "ogg", "oga", "opus", "wav", "flac"];
// Streaming playlists and their small pieces cannot be saved as one file yet.
const STREAM_TYPES = ["m3u8", "mpd", "ts", "m4s"];

export function header(headers, name) {
  const h = (headers || []).find((x) => x.name.toLowerCase() === name);
  return h ? h.value : "";
}

/** Is this network response a whole audio or video file worth offering? */
export function isMedia(url, contentType, length) {
  if (!/^https?:\/\//i.test(url || "")) return false;
  const ext = extensionOf(url);
  const type = String(contentType || "").toLowerCase();
  if (STREAM_TYPES.includes(ext) || /mpegurl|dash\+xml|mp2t/.test(type)) return false;
  if (length >= 0 && length < 300 * 1024) return false;
  return /^(video|audio)\//.test(type) || MEDIA_TYPES.includes(ext);
}

export function nameFromUrl(url) {
  try {
    // A decoded name can hold path separators or control characters (%2F, %5C, %00): never let them through.
    const base = decodeURIComponent(new URL(url).pathname.split("/").pop() || "")
      .replace(/[\\/\u0000-\u001f]+/g, " ").trim().slice(0, 200);
    return base && base !== "." && base !== ".." ? base : "download";
  } catch (_) {
    return "download";
  }
}

export function formatSize(bytes) {
  if (!(bytes > 0)) return "";
  const units = ["B", "KB", "MB", "GB"];
  let v = bytes;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v.toFixed(v >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
}

/** Keeps web links whose file type is listed (all file links when the list is empty), without duplicates. */
export function filterLinks(links, typesText) {
  const types = parseTypes(typesText);
  const seen = new Set();
  const out = [];
  for (const raw of links || []) {
    const url = String(raw).split("#")[0];
    if (!/^https?:\/\//i.test(url) || seen.has(url)) continue;
    const ext = extensionOf(url);
    if (types.length ? !types.includes(ext) : !ext || ["html", "htm", "php", "asp", "aspx"].includes(ext)) continue;
    seen.add(url);
    out.push(url);
  }
  return out;
}

/** Adds a media item to a tab's list (newest first, no duplicates, at most 30). */
export function addMedia(list, item) {
  const rest = (list || []).filter((m) => m.url !== item.url);
  return [item, ...rest].slice(0, 30);
}

/** Is this response a streamed-video playlist (HLS)? */
/**
 * Whether a script request (XHR or fetch) could be audio, video or a stream playlist, judged from its address or its
 * content type alone, so it is cheap to ask for every request. Many players load playlists from addresses without a
 * file extension (for example /playlist?id=1), which only the content type gives away.
 */
export function mayBeMedia(url, contentType) {
  if (!/^https?:\/\//i.test(url || "")) return false;
  const type = String(contentType || "").toLowerCase();
  if (/mpegurl|dash\+xml|^(video|audio)\//.test(type)) return true;
  const ext = extensionOf(url);
  return ext === "m3u8" || ext === "mpd" || MEDIA_TYPES.includes(ext);
}

/** The origin of an address ("https://e.com"), or "" when it isn't one. */
export function originOf(url) {
  try {
    const u = new URL(url);
    return /^https?:$/.test(u.protocol) ? u.origin : "";
  } catch (_) {
    return "";
  }
}

/** Initialization fragments are components of a stream, not standalone videos. */
export function isStreamInitialization(url) {
  if (!isWebLink(url)) return false;
  try {
    const name = decodeURIComponent(new URL(url).pathname.split("/").pop() || "").toLowerCase();
    return /^(?:init|initialization)(?:[-_.][a-z0-9-]+)?\.(?:mp4|m4a|m4s)$/.test(name);
  } catch { return false; }
}

export function isStreamPlaylist(url, contentType) {
  if (!/^https?:\/\//i.test(url || "")) return false;
  return extensionOf(url) === "m3u8" || /mpegurl/i.test(String(contentType || ""));
}

/** Reads a playlist's text: best quality on offer, the variant links it points to, and whether it is protected or live. */
export function summarizePlaylist(text, base) {
  const lines = String(text || "").split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  const out = { valid: lines[0] === "#EXTM3U", master: false, height: 0, bandwidth: 0, variants: [], protected: false, live: false };
  if (!out.valid) return out;
  let pending = null;
  for (const line of lines) {
    if (line.startsWith("#EXT-X-STREAM-INF")) {
      out.master = true;
      const res = /RESOLUTION=\d+x(\d+)/i.exec(line);
      const bw = /BANDWIDTH=(\d+)/i.exec(line);
      pending = { height: res ? Number(res[1]) : 0, bandwidth: bw ? Number(bw[1]) : 0 };
    } else if (/^#EXT-X-(SESSION-)?KEY/i.test(line)) {
      const m = /METHOD=([A-Z0-9-]+)/i.exec(line);
      if (m && m[1].toUpperCase() !== "NONE") out.protected = true;
    } else if (!line.startsWith("#") && pending) {
      try {
        out.variants.push(new URL(line, base).href);
      } catch (_) {
        /* not a link */
      }
      if (pending.height > out.height || (pending.height === out.height && pending.bandwidth > out.bandwidth)) {
        out.height = pending.height;
        out.bandwidth = pending.bandwidth;
      }
      pending = null;
    }
  }
  if (!out.master) out.live = !lines.includes("#EXT-X-ENDLIST");
  return out;
}

/** A file name from a page title: "My Talk | Site" becomes "My Talk". */
export function titleToName(title, fallback = "video") {
  const t = String(title || "").split(/\s[|–—-]\s/)[0].replace(/[\\/:*?"<>|\u0000-\u001f]+/g, " ").replace(/\s+/g, " ").trim();
  return (t && t !== "." && t !== ".." ? t : fallback).slice(0, 120);
}

/** Best first: streams by picture height, then files by size. */
export function sortMedia(list) {
  return [...(list || [])].sort((a, b) => (b.height || 0) - (a.height || 0) || (b.size || 0) - (a.size || 0));
}

export function qualityLabel(item) {
  if (item.kind !== "stream") return formatSize(item.size);
  const parts = [item.height ? `${item.height}p` : "", item.live ? "live" : "", "stream"].filter(Boolean);
  return parts.join(" · ");
}

/** Sites whose videos the app saves with its video engine (best quality, picture and sound joined). */
export const VIDEO_SITES = [
  "youtube.com", "youtu.be", "youtube-nocookie.com", "instagram.com", "facebook.com", "fb.watch", "x.com", "twitter.com",
  "tiktok.com", "vimeo.com", "dailymotion.com", "reddit.com", "redd.it", "twitch.tv", "soundcloud.com", "linkedin.com",
  "pinterest.com", "threads.net", "bilibili.com", "rumble.com", "streamable.com", "bandcamp.com", "mixcloud.com",
];

export function isVideoSite(url) {
  try {
    const host = new URL(url).hostname.toLowerCase();
    return VIDEO_SITES.some((h) => host === h || host.endsWith(`.${h}`));
  } catch (_) {
    return false;
  }
}

