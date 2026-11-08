import assert from "node:assert/strict";
import test from "node:test";
import { cookieHeader, downloadKey, extensionOf, isWebLink, mayBeMedia, originOf, shouldTakeOver, siteListed, siteOf } from "./helpers.js";

test("only web links go to the app", () => {
  assert.equal(isWebLink("https://example.com/a.zip"), true);
  assert.equal(isWebLink("HTTP://example.com/a"), true);
  for (const bad of ["file:///etc/passwd", "javascript:alert(1)", "data:text/html,x", "chrome://settings", "ftp://e.com/a", "https://", "not a url"])
    assert.equal(isWebLink(bad), false, bad);
});

test("takes over normal web downloads only", () => {
  const big = { url: "https://example.com/a.zip", filename: "/Users/x/Downloads/a.zip", totalBytes: 50e6 };
  assert.equal(shouldTakeOver(big, {}), true);
  assert.equal(shouldTakeOver({ ...big, url: "blob:https://example.com/1" }, {}), false);
  assert.equal(shouldTakeOver({ ...big, totalBytes: 1000 }, {}), false);
  assert.equal(shouldTakeOver({ ...big, totalBytes: -1 }, {}), true);
  assert.equal(shouldTakeOver(big, { enabled: false }), false);
  assert.equal(shouldTakeOver(big, { skipTypes: ".ZIP, pdf" }), false);
});

test("file extensions", () => {
  assert.equal(extensionOf("https://e.com/x/Movie.MP4?token=1"), "mp4");
  assert.equal(extensionOf("/Users/x/Downloads/archive.tar.gz"), "gz");
  assert.equal(extensionOf("README"), "");
});

test("cookie header", () => {
  assert.equal(cookieHeader([{ name: "a", value: "1" }, { name: "b", value: "2" }]), "a=1; b=2");
  assert.equal(cookieHeader([]), "");
});

import { addMedia, filterLinks, formatSize, isMedia, nameFromUrl } from "./helpers.js";

test("media detection", () => {
  assert.equal(isMedia("https://e.com/v/clip.mp4", "", -1), true);
  assert.equal(isMedia("https://e.com/stream", "video/webm", 5e6), true);
  assert.equal(isMedia("https://e.com/live/index.m3u8", "application/vnd.apple.mpegurl", -1), false);
  assert.equal(isMedia("https://e.com/seg/001.ts", "video/mp2t", 1e6), false);
  assert.equal(isMedia("https://e.com/tiny.mp3", "audio/mpeg", 1000), false);
  assert.equal(isMedia("blob:https://e.com/1", "video/mp4", -1), false);
});

test("link filtering", () => {
  const links = ["https://e.com/a.zip", "https://e.com/a.zip#x", "https://e.com/page.html", "https://e.com/b.PDF", "mailto:x@e.com", "https://e.com/dir/"];
  assert.deepEqual(filterLinks(links, ""), ["https://e.com/a.zip", "https://e.com/b.PDF"]);
  assert.deepEqual(filterLinks(links, "pdf"), ["https://e.com/b.PDF"]);
});

test("small helpers", () => {
  assert.equal(nameFromUrl("https://e.com/x/My%20Song.mp3?a=1"), "My Song.mp3");
  assert.equal(formatSize(1536), "1.5 KB");
  assert.equal(formatSize(-1), "");
  const list = addMedia(addMedia([], { url: "a" }), { url: "b" });
  assert.deepEqual(addMedia(list, { url: "a" }).map((m) => m.url), ["a", "b"]);
});

import { isStreamInitialization, isStreamPlaylist, qualityLabel, sortMedia, summarizePlaylist, titleToName } from "./helpers.js";

test("stream playlists", () => {
  assert.equal(isStreamPlaylist("https://e.com/v/master.m3u8?t=1", ""), true);
  assert.equal(isStreamPlaylist("https://e.com/v/play", "application/vnd.apple.mpegurl"), true);
  assert.equal(isStreamPlaylist("https://e.com/v/a.mp4", "video/mp4"), false);
  const master = summarizePlaylist(
    "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nhd.m3u8\n",
    "https://e.com/v/master.m3u8",
  );
  assert.equal(master.master, true);
  assert.equal(master.height, 1080);
  assert.deepEqual(master.variants, ["https://e.com/v/low.m3u8", "https://e.com/v/hd.m3u8"]);
  const locked = summarizePlaylist('#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://k"\n#EXTINF:6,\na.ts\n', "https://e.com/");
  assert.equal(locked.protected, true);
  assert.equal(locked.live, true);
  assert.equal(summarizePlaylist("<html>", "https://e.com/").valid, false);
});

test("names, order and labels", () => {
  assert.equal(titleToName("My Talk: Part 1 | Conference"), "My Talk Part 1");
  assert.equal(titleToName(""), "video");
  const sorted = sortMedia([{ url: "a", size: 10 }, { url: "b", kind: "stream", height: 720 }, { url: "c", kind: "stream", height: 1080 }]);
  assert.deepEqual(sorted.map((m) => m.url), ["c", "b", "a"]);
  assert.equal(qualityLabel({ kind: "stream", height: 1080 }), "1080p · stream");
});

import { isVideoSite } from "./helpers.js";

test("video sites", () => {
  assert.equal(isVideoSite("https://www.youtube.com/watch?v=1"), true);
  assert.equal(isVideoSite("https://m.facebook.com/reel/1"), true);
  assert.equal(isVideoSite("https://notyoutube.com/"), false);
  assert.equal(isVideoSite("not a link"), false);
});


test("sites kept in the browser, and pausing", () => {
  const item = { url: "https://cdn.example.com/a.zip", referrer: "https://www.other.org/page", totalBytes: 50e6 };
  assert.equal(shouldTakeOver(item, { skipSites: "example.com" }), false);
  assert.equal(shouldTakeOver(item, { skipSites: "other.org" }), false, "the page the link was on counts too");
  assert.equal(shouldTakeOver(item, { skipSites: "notexample.com, sample.com" }), true);
  assert.equal(shouldTakeOver(item, { pausedUntil: 2000, now: 1000 }), false);
  assert.equal(shouldTakeOver(item, { pausedUntil: 500, now: 1000 }), true);
  assert.equal(siteListed("https://example.com/x", " *.Example.com:443 "), true);
  assert.equal(siteListed("not a link", "example.com"), false);
  assert.equal(siteOf("https://Files.Example.org/path?x"), "files.example.org");
});

test("the same download is recognized", () => {
  assert.equal(downloadKey("https://e.com/a.zip#start"), downloadKey("https://e.com/a.zip"));
  assert.notEqual(downloadKey("https://e.com/a.zip?v=1"), downloadKey("https://e.com/a.zip?v=2"));
});

test("script requests that may be media: by address or content type", () => {
  assert.equal(mayBeMedia("https://cdn.e.com/hls/playlist?id=7", "application/vnd.apple.mpegurl"), true);
  assert.equal(mayBeMedia("https://cdn.e.com/stream", "application/x-mpegURL; charset=utf-8"), true);
  assert.equal(mayBeMedia("https://cdn.e.com/manifest", "application/dash+xml"), true);
  assert.equal(mayBeMedia("https://cdn.e.com/v/1", "video/mp4"), true);
  assert.equal(mayBeMedia("https://cdn.e.com/master.m3u8?t=1", "text/plain"), true);
  assert.equal(mayBeMedia("https://e.com/api/user", "application/json"), false);
  assert.equal(mayBeMedia("https://e.com/app.js", "text/javascript"), false);
  assert.equal(mayBeMedia("blob:https://e.com/1", "video/mp4"), false);
});

test("origins", () => {
  assert.equal(originOf("https://e.com/a/b?c"), "https://e.com");
  assert.equal(originOf("chrome://settings"), "");
  assert.equal(originOf("nope"), "");
});

test("HLS initialization fragments do not appear as downloadable videos", () => {
  assert.equal(isStreamInitialization("https://playback.example/hls/init.mp4?token=x"), true);
  assert.equal(isStreamInitialization("https://cdn.example/audio/initialization.m4a"), true);
  assert.equal(isStreamInitialization("https://cdn.example/audio/init-1.m4s"), true);
  assert.equal(isStreamInitialization("https://cdn.example/movie.mp4"), false);
  assert.equal(isStreamInitialization("https://cdn.example/start.mp4"), false);
  assert.equal(isStreamInitialization("blob:https://cdn.example/init.mp4"), false);
});
