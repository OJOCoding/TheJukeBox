// Service worker.
//
// ===========================================================================
// The two rules that matter most are both about audio
// ===========================================================================
//
// 1. NEVER handle a request carrying a Range header.
// 2. NEVER handle the audio stream route at all.
//
// `cache.put()` throws a TypeError for any response with status 206. The streaming endpoint
// answers 206 to every seek, so a naive "cache all GETs" handler does not merely fail to
// help -- it breaks playback outright. Responding to a range request from a worker would
// also mean slicing bodies and synthesising 206s with correct Content-Range by hand, which
// is a great deal of machinery to reimplement badly.
//
// Letting those requests fall straight through to the network costs nothing: the browser's
// own HTTP cache still serves them, and the server sends a revalidating Cache-Control
// precisely so that works.
//
// Offline playback, when it arrives, will store whole files as blobs in IndexedDB and
// resolve them in the app rather than here, so 206 never enters the picture.

// Bumped whenever PRECACHE changes: the install handler only re-runs addAll for a cache name
// it has not seen, so leaving the version alone would keep serving the old shell to everyone
// who already has the worker installed.
const VERSION = 'v4';

const SHELL_CACHE = `jb-shell-${VERSION}`;
const ART_CACHE = `jb-art-${VERSION}`;

/** The app shell. Enough to boot offline and explain itself. */
const PRECACHE = [
    '/',
    '/css/tokens.css',
    '/css/base.css',
    '/css/app.css',
    '/js/main.js',
    '/js/core/ui.js',
    '/js/core/store.js',
    '/js/core/api.js',
    '/js/core/router.js',
    '/js/player/queue.js',
    '/js/player/controller.js',
    '/js/player/mediasession.js',
    '/js/player/persistence.js',
    '/js/ui/virtual-list.js',
    '/js/views/welcome.js',
    '/js/views/library.js',
    '/js/views/admin.js',
    '/js/views/queue.js',
    '/js/views/playlists.js',
    '/js/views/browse.js',
    '/manifest.webmanifest',
    '/favicon.svg',
    '/icons/icon-192.png',
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(SHELL_CACHE).then((cache) =>
            // addAll is atomic: one 404 fails the whole install, which is what you want,
            // because a partially precached shell fails in far stranger ways later.
            cache.addAll(PRECACHE)),
    );

    // Deliberately no skipWaiting() here. See the message handler at the bottom.
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        (async () => {
            const names = await caches.keys();

            await Promise.all(
                names
                    .filter((name) => name.startsWith('jb-') && !name.endsWith(VERSION))
                    .map((name) => caches.delete(name)),
            );

            await self.clients.claim();
        })(),
    );
});

self.addEventListener('fetch', (event) => {
    const request = event.request;

    // Rule 1. Range requests go straight to the network, untouched.
    if (request.headers.has('range')) return;

    if (request.method !== 'GET') return;

    const url = new URL(request.url);

    // Same-origin only. A cross-origin request is not ours to manage.
    if (url.origin !== self.location.origin) return;

    // Rule 2. Audio never passes through here, with or without a Range header.
    if (url.pathname.startsWith('/api/tracks/') && url.pathname.endsWith('/stream')) return;

    // Cover art is content-addressed, so a URL's bytes can never change. Cache-first, and
    // it can be kept indefinitely.
    if (url.pathname.startsWith('/api/art/')) {
        event.respondWith(cacheFirst(request, ART_CACHE));
        return;
    }

    // Everything else under /api is live data: the session, the library, sync status. Never
    // served from a cache, because a stale library or a stale session is worse than an
    // error -- it looks like the app is working and lying.
    if (url.pathname.startsWith('/api/')) return;

    // Navigations: network first so a deployed change is picked up, falling back to the
    // cached shell when offline. The client router resolves the path either way.
    if (request.mode === 'navigate') {
        event.respondWith(networkFirstShell(request));
        return;
    }

    event.respondWith(staleWhileRevalidate(request, SHELL_CACHE));
});

async function cacheFirst(request, cacheName) {
    const cache = await caches.open(cacheName);
    const hit = await cache.match(request);

    if (hit) return hit;

    const response = await fetch(request);

    // Only 200 is cacheable. Guarding on status is what keeps a 206 or an opaque redirect
    // from reaching cache.put and throwing.
    if (response.ok && response.status === 200) {
        cache.put(request, response.clone());
    }

    return response;
}

async function networkFirstShell(request) {
    try {
        return await fetch(request);
    } catch {
        const cache = await caches.open(SHELL_CACHE);

        // Any deep link falls back to the shell, matching the server's own fallback.
        return (await cache.match('/')) ?? Response.error();
    }
}

async function staleWhileRevalidate(request, cacheName) {
    const cache = await caches.open(cacheName);
    const hit = await cache.match(request);

    const refresh = fetch(request)
        .then((response) => {
            if (response.ok && response.status === 200) {
                cache.put(request, response.clone());
            }
            return response;
        })
        .catch(() => null);

    return hit ?? (await refresh) ?? Response.error();
}

// The update flow is explicitly user-driven.
//
// Calling skipWaiting() on install would activate a new worker under a live page, and in a
// music player that means the audio element can be torn down mid-song. Unforgivable for the
// sake of shipping a CSS change a few seconds earlier. Instead the page notices the waiting
// worker, offers a reload, and defers while something is playing; a cold start picks the
// new version up regardless.
self.addEventListener('message', (event) => {
    if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});
