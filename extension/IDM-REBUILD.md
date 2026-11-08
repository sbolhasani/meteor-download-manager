# Chrome IDM reference → Meteor independent extension rebuild

Reference inspected: user-supplied Chrome IDM Integration Module 6.43.1 CRX, **not** the legacy Edge CRX.

This branch intentionally **replaces** the Meteor extension's background/content implementation rather than applying the earlier HLS-only patch. It does not embed or call IDM's proprietary JavaScript or native host. It retains the existing popup, helper API, extension key and Meteor native-host protocol for compatibility with the desktop app.

## Reconstructed behaviors
- Manifest V3 service worker and all-frame document-start content integration.
- Background network monitoring, request context tracking, media/content-type detection, response handling, request cleanup.
- In-page fetch/XHR candidate discovery through a small injected page observer.
- Video overlay with page-media list, stream selection and site-video fallback.
- Download takeover with native acceptance and browser fallback.
- Native Messaging, context menus, per-tab media storage, popup protocol compatibility.

## Known limits / validation pending
- **Not** claimed as feature-equivalent to IDM. Chrome IDM includes many extra branches, native-host responsibilities and behaviors not covered here.
- DASH manifests are recognized by observer but not offered as HLS or handed directly to the native engine; future DASH implementation requires separate validation.
- The page observer wraps fetch/XHR, which may be incompatible with some sites. Further site tests are required.
- Playlist fetches without page-specific authentication may not be readable from a service worker; candidates are shown but native HLS still needs proper request headers.
- Do not use this extension to circumvent DRM.
- Run `cd extension && npm test` and manually test Chrome/Edge installation, full-file downloads, master and media HLS playlists, worker/iframe playback, protected media and native-host absence before merging.
