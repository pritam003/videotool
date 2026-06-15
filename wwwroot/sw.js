// Service Worker for avideo.
//
// Purpose: receive Web Push messages from the .NET backend and surface them as
// OS-level notifications (lock screen + status bar) when the page is hidden,
// backgrounded, or the screen is off. Tapping a notification re-focuses the
// open tab (or opens a new one), which lets the page's interruptible-sleep +
// Wake Lock logic resume the JS render loop for the next chunk.
//
// Notes:
// - On Android Chrome this works for any tab.
// - On iOS 16.4+ Safari it works ONLY when the site has been added to the
//   Home Screen as a PWA (Apple restriction). Regular Safari tabs cannot
//   receive Web Push at all.

self.addEventListener("install", e => { self.skipWaiting(); });
self.addEventListener("activate", e => { e.waitUntil(self.clients.claim()); });

self.addEventListener("push", event => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch { data = { title: "avideo", body: event.data ? event.data.text() : "" }; }
  const title = data.title || "avideo";
  const body  = data.body  || "";
  const tag   = data.tag   || "avideo";
  const opts = {
    body,
    tag,                      // same tag → replaces the previous notification
    renotify: true,           // vibrate / re-announce on update
    requireInteraction: false,
    icon: "/favicon.ico",
    badge: "/favicon.ico",
    data: { jobId: data.jobId, ts: data.ts || Date.now() },
  };
  event.waitUntil(self.registration.showNotification(title, opts));
});

self.addEventListener("notificationclick", event => {
  event.notification.close();
  // Focus an existing tab if one is open; otherwise open a fresh one.
  event.waitUntil((async () => {
    const all = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const c of all) {
      if (c.url.includes(self.location.origin)) {
        // Hand control back to the page so it can resume polling immediately.
        c.postMessage({ type: "avideo:notification-click", jobId: event.notification.data?.jobId });
        return c.focus();
      }
    }
    return self.clients.openWindow("/");
  })());
});
