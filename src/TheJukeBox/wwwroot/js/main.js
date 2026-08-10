// Application entry point.

import { alertUser, announce, mmss, spoken } from './core/ui.js';
import { api, handleUnauthenticatedWith, isAdmitted, isReady } from './core/api.js';
import { createRouter } from './core/router.js';
import { createStore } from './core/store.js';
import { showShortcuts } from './ui/dialog.js';
import { bindMediaSession } from './player/mediasession.js';
import { createPersistence } from './player/persistence.js';
import { createPlayer } from './player/controller.js';
import { renderAdmin } from './views/admin.js';
import { renderAlbum, renderAlbums, renderArtist, renderArtists } from './views/browse.js';
import { renderLibrary } from './views/library.js';
import { renderNowPlaying } from './views/now-playing.js';
import { renderPlaylistDetail, renderPlaylists } from './views/playlists.js';
import { renderQueue } from './views/queue.js';
import { renderSyncProgress, renderWelcome } from './views/welcome.js';

const dom = {
    view: document.getElementById('view'),
    audio: document.getElementById('audio'),
    topbar: document.getElementById('topbar'),
    transport: document.getElementById('transport'),
    nowArt: document.getElementById('nowArt'),
    nowTitle: document.getElementById('nowTitle'),
    nowSub: document.getElementById('nowSub'),
    playButton: document.getElementById('playButton'),
    playGlyph: document.getElementById('playGlyph'),
    prevButton: document.getElementById('prevButton'),
    nextButton: document.getElementById('nextButton'),
    shuffleButton: document.getElementById('shuffleButton'),
    repeatButton: document.getElementById('repeatButton'),
    seek: document.getElementById('seek'),
    position: document.getElementById('position'),
    duration: document.getElementById('duration'),
    volume: document.getElementById('volume'),
    volumeControls: document.getElementById('volumeControls'),
    muteButton: document.getElementById('muteButton'),
    syncButton: document.getElementById('syncButton'),
    syncChip: document.getElementById('syncChip'),
    signOutButton: document.getElementById('signOutButton'),
    shortcutsButton: document.getElementById('shortcutsButton'),
    adminLink: document.getElementById('adminLink'),
    profileButton: document.getElementById('profileButton'),
    profileMenu: document.getElementById('profileMenu'),
    profileAvatar: document.getElementById('profileAvatar'),
    profileInitial: document.getElementById('profileInitial'),
    profileName: document.getElementById('profileName'),
    profileEmail: document.getElementById('profileEmail'),
};

// Fail loudly, at once, if the markup and this map have drifted apart.
//
// Without this, a missing entry is `undefined`, the first property access on it throws deep
// inside renderChrome, boot() rejects, the router never starts, and every visitor gets a
// blank page with nothing in the console unless they are already watching. That is exactly
// what a missing `nowArt` entry did: the app was completely broken and every check that did
// not require boot() to finish still passed.
const missingElements = Object.entries(dom)
    .filter(([, element]) => element === null)
    .map(([name]) => name);

if (missingElements.length > 0) {
    throw new Error(
        `index.html is missing element(s) required by main.js: ${missingElements.join(', ')}. `
        + 'The id in the markup and the key in the dom map must match.');
}

const store = createStore({
    session: null,
    currentTrackId: null,
    currentTrack: null,
    playing: false,
    buffering: false,
    position: 0,
    duration: 0,
    mode: { shuffle: false, repeat: 'off' },
    hasNext: false,
    hasPrevious: false,
    trackCount: 0,

    // Bumped by the player whenever the queue changes, so views can re-render on a change
    // rather than on every position tick.
    queueRevision: 0,
    queueLength: 0,

    // The path currently rendered. Set by the router, read by the chrome.
    route: window.location.pathname,
});

const player = createPlayer({ audio: dom.audio, store });

const persistence = createPersistence({ player, store });

// ---------------------------------------------------------------------------
// Routing
// ---------------------------------------------------------------------------

const routes = [
    { path: '/', title: 'Welcome', render: (outlet) => renderWelcome(outlet, { store, onSync: requestSync }) },
    { path: '/library', title: 'Library', render: (outlet) => renderLibrary(outlet, { store, player, onSync: requestSync }) },
    { path: '/now', title: 'Now playing', render: (outlet) => renderNowPlaying(outlet, { store, player }) },
    { path: '/albums', title: 'Albums', render: (outlet) => renderAlbums(outlet, { store, player }) },
    {
        path: '/albums/:key',
        title: 'Album',
        render: (outlet, params) => renderAlbum(outlet, { store, player }, params),
    },
    { path: '/artists', title: 'Artists', render: (outlet) => renderArtists(outlet, { store, player }) },
    {
        path: '/artists/:key',
        title: 'Artist',
        render: (outlet, params) => renderArtist(outlet, { store, player }, params),
    },
    { path: '/queue', title: 'Queue', render: (outlet) => renderQueue(outlet, { store, player }) },
    { path: '/playlists', title: 'Playlists', render: (outlet) => renderPlaylists(outlet, { store, player }) },

    // Declared after /playlists so the static route is the obvious one to read first; the
    // router prefers exact matches regardless of order.
    {
        path: '/playlists/:id',
        title: 'Playlist',
        render: (outlet, params) => renderPlaylistDetail(outlet, { store, player }, params),
    },
    { path: '/sync', title: 'Initial sync', render: (outlet) => renderSyncProgress(outlet, { onDone: onSyncFinished }) },
    { path: '/admin', title: 'Accounts', render: (outlet) => renderAdmin(outlet, { store }) },
];

const router = createRouter({
    outlet: dom.view,
    routes,

    // Recorded in the store so the chrome can react to where the user is -- the transport hides on
    // the landing page, which is a property of the route rather than of playback.
    onNavigated: (pathname) => store.set({ route: pathname }),

    resolve(pathname, route) {
        const session = store.state.session;

        // Until the bootstrap call lands, render nothing rather than flashing the
        // signed-out view at someone who is signed in.
        if (session === null) return { route: null };

        // Awaiting approval, or disabled: the welcome view is the only one that can say
        // anything useful, and every other view would just render 403s. The admin screen is
        // no exception -- a pending administrator has no admin access either.
        if (!isAdmitted(session)) {
            return pathname === '/' ? { route: routes[0] } : { redirectTo: '/' };
        }

        // Signed out, or no Drive grant: only the welcome view makes sense.
        if (!isReady(session)) return pathname === '/' ? { route: routes[0] } : { redirectTo: '/' };

        // An unrecognised path lands on the library. This is also what catches bookmarks
        // to the retired /player page.
        if (!route) return { redirectTo: '/library' };

        // Deep-linking /admin as a non-administrator lands on the library rather than a
        // permission error. The server refuses the calls regardless; this only keeps the UI
        // from rendering a screen it cannot populate.
        if (pathname === '/admin' && session.isAdmin !== true) return { redirectTo: '/library' };

        if (pathname === '/' && store.state.trackCount > 0) return { redirectTo: '/library' };

        return { route };
    },
});

// ---------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------

dom.playButton.addEventListener('click', () => player.toggle());
dom.nextButton.addEventListener('click', () => player.next());
dom.prevButton.addEventListener('click', () => player.previous());

dom.shuffleButton.addEventListener('click', () => {
    player.setShuffle(dom.shuffleButton.getAttribute('aria-pressed') !== 'true');
});

// Repeat has three states, so aria-pressed cannot express it; the accessible name
// carries the state instead and the visible label changes with it.
dom.repeatButton.addEventListener('click', () => player.cycleRepeat());

dom.seek.addEventListener('pointerdown', () => player.beginSeek());
dom.seek.addEventListener('keydown', () => player.beginSeek());

// Updating aria-valuetext on input, but only seeking on change, keeps a key-repeat or a
// drag from issuing a new ranged HTTP request per event and thrashing playback.
dom.seek.addEventListener('input', () => {
    const value = Number(dom.seek.value);
    dom.position.textContent = mmss(value);
    dom.seek.setAttribute('aria-valuetext', `${spoken(value)} of ${spoken(store.state.duration)}`);
});

dom.seek.addEventListener('change', () => player.endSeek(Number(dom.seek.value)));

// On input, not on change: volume is instantaneous and costs nothing, unlike a seek, which
// issues a fresh ranged request. Dragging it should be audible while you drag.
dom.volume.addEventListener('input', () => player.setVolume(Number(dom.volume.value) / 100));

dom.muteButton.addEventListener('click', () => player.toggleMute());

dom.syncButton.addEventListener('click', requestSync);

dom.shortcutsButton.addEventListener('click', () => void showShortcuts());

dom.signOutButton.addEventListener('click', async () => {
    try {
        await api.signOut();
    } finally {
        // A full navigation, not a router push: signing out must drop every trace of the
        // previous session's state from memory.
        window.location.assign('/');
    }
});

store.subscribe(renderChrome);

function renderChrome(state) {
    const ready = isReady(state.session);

    // The landing page carries none of the app's chrome. It is a description of the project for
    // someone who has not signed in or has nothing indexed yet, so a nav, an account menu, a sync
    // button and a set of playback controls are all furniture with nothing behind them.
    const landing = state.route === '/';

    dom.topbar.hidden = !ready || landing;
    dom.transport.hidden = !ready || landing;

    // A menu left open across a navigation would reappear over the new view.
    if (landing) setMenuOpen(false);

    renderAccountMenu(state);

    dom.shuffleButton.setAttribute('aria-pressed', state.mode.shuffle ? 'true' : 'false');

    // Repeat-one and shuffle are mutually exclusive in effect, not just in spirit: repeat-one
    // never advances the queue, so the play order shuffle controls is never consulted. Leaving the
    // control live would let the user turn on a mode that visibly does nothing.
    const repeatingOne = state.mode.repeat === 'one';

    dom.shuffleButton.disabled = repeatingOne;
    dom.shuffleButton.title = repeatingOne
        ? 'Shuffle does nothing while repeating one track'
        : '';

    const track = state.currentTrack;

    dom.nowTitle.textContent = track ? track.title : 'SELECT TRACK TO BEGIN';
    dom.nowSub.textContent = track
        ? [track.artist, track.album].filter(Boolean).join(' — ')
        : '';

    dom.playGlyph.textContent = state.playing ? '❚❚' : '▶';

    // The accessible name describes the action, not the state, which is what a screen
    // reader user needs from a play/pause control.
    dom.playButton.setAttribute('aria-label', state.playing ? 'Pause' : 'Play');

    dom.nextButton.disabled = !state.hasNext;
    dom.prevButton.disabled = !state.hasPrevious && state.position < 3;

    const repeat = state.mode.repeat;
    dom.repeatButton.dataset.state = repeat;
    dom.repeatButton.textContent = repeat === 'off' ? 'REPEAT' : repeat === 'all' ? 'REPEAT ALL' : 'REPEAT ONE';
    dom.repeatButton.setAttribute(
        'aria-label',
        `Repeat: ${repeat}. Activate for repeat ${repeat === 'off' ? 'all' : repeat === 'all' ? 'one' : 'off'}.`,
    );

    renderArt(track);

    dom.duration.textContent = mmss(state.duration);
    dom.seek.max = String(Math.floor(state.duration || 0));

    // Left alone while dragging, so the thumb does not fight the user.
    if (document.activeElement !== dom.seek) {
        dom.seek.value = String(Math.floor(state.position || 0));
        dom.position.textContent = mmss(state.position);
    }

    renderVolume(state);
}

/**
 * Draws the volume control from the element's actual output level.
 *
 * Read from the store rather than from our own last write, because the OS media keys and the
 * browser's tab-mute both change the audio element directly. A slider that only reflects its
 * own drags is a slider that disagrees with what you can hear.
 */
function renderVolume(state) {
    // Absent, not disabled. There is nothing the user can do about iOS refusing the assignment,
    // so a greyed-out slider would only invite them to try.
    dom.volumeControls.hidden = state.volumeSupported === false;

    if (state.volumeSupported === false) return;

    const muted = state.muted === true;
    const percent = Math.round((state.volume ?? 1) * 100);

    // Muted shows zero rather than the level it will return to. What you hear is the truth the
    // control should be telling.
    const shown = muted ? 0 : percent;

    if (document.activeElement !== dom.volume) {
        dom.volume.value = String(shown);
    }

    dom.volume.setAttribute('aria-valuetext', muted ? 'Muted' : `${percent} percent`);

    dom.muteButton.dataset.muted = muted ? 'true' : 'false';
    dom.muteButton.setAttribute('aria-pressed', muted ? 'true' : 'false');
    dom.muteButton.setAttribute('aria-label', muted ? 'Unmute' : 'Mute');
}

/**
 * Fills in the account menu from the session.
 *
 * The avatar is Google's, so it may fail to load or be absent entirely; the initial behind it is
 * what the menu falls back to rather than an empty square.
 */
function renderAccountMenu(state) {
    const user = state.session?.user ?? null;
    const ready = isReady(state.session);

    // The admin link only exists for administrators, and is removed again if a session reload says
    // otherwise -- a disabled administrator must not keep the link to the screen that would
    // re-enable them.
    dom.adminLink.hidden = !(ready && state.session?.isAdmin === true);

    const name = user?.displayName || user?.email || 'Account';

    dom.profileName.textContent = name;
    dom.profileEmail.textContent = user?.email ?? '';
    dom.profileInitial.textContent = name.trim().charAt(0).toUpperCase() || '?';

    dom.profileButton.setAttribute('aria-label', `Account: ${name}`);

    if (user?.pictureUrl && dom.profileAvatar.dataset.src !== user.pictureUrl) {
        dom.profileAvatar.dataset.src = user.pictureUrl;
        dom.profileAvatar.hidden = false;
        dom.profileAvatar.src = user.pictureUrl;

        // Falls back to the initial rather than leaving a broken image. Google's avatar URLs
        // expire, so this is a normal outcome rather than an edge case.
        dom.profileAvatar.addEventListener('error', () => { dom.profileAvatar.hidden = true; }, { once: true });
    } else if (!user?.pictureUrl) {
        dom.profileAvatar.hidden = true;
    }
}

/** Opens and closes the account menu, keeping aria-expanded truthful. */
function setMenuOpen(open) {
    dom.profileMenu.hidden = !open;
    dom.profileButton.setAttribute('aria-expanded', open ? 'true' : 'false');
}

dom.profileButton.addEventListener('click', (event) => {
    event.stopPropagation();
    setMenuOpen(dom.profileMenu.hidden);
});

// Closing on an outside click and on Escape are both required for a menu to feel like one, and
// Escape additionally returns focus to the trigger so a keyboard user is not stranded.
document.addEventListener('click', (event) => {
    if (dom.profileMenu.hidden) return;
    if (event.target instanceof Node && dom.profileMenu.contains(event.target)) return;

    setMenuOpen(false);
});

document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || dom.profileMenu.hidden) return;

    setMenuOpen(false);
    dom.profileButton.focus();
});

// Following a link inside the menu must close it, or it stays open over the view it just opened.
dom.profileMenu.addEventListener('click', (event) => {
    if (event.target instanceof Element && event.target.closest('a, button')) setMenuOpen(false);
});

/**
 * Shows the album art, falling back to a glyph.
 *
 * The frame has always been styled for a 48px image; before B1 it held a static emoji
 * identical for every track, because nothing fetched cover art.
 */
function renderArt(track) {
    const frame = dom.nowArt;

    if (!track?.artSha256) {
        frame.replaceChildren(document.createTextNode('♪'));
        return;
    }

    const existing = frame.querySelector('img');

    // Guarded, or every store update (including the position ticker) would replace the
    // element and restart the image load.
    if (existing?.dataset.sha === track.artSha256) return;

    const image = new Image();
    image.src = `/api/art/${track.artSha256}/96`;
    image.alt = '';
    image.width = 48;
    image.height = 48;
    image.dataset.sha = track.artSha256;

    // Decorative: the title and artist are already announced as text beside it, so alt is
    // empty rather than duplicating them.
    image.addEventListener('error', () => frame.replaceChildren(document.createTextNode('♪')));
    image.addEventListener('load', () => frame.replaceChildren(image));
}

// ---------------------------------------------------------------------------
// Global keyboard shortcuts
// ---------------------------------------------------------------------------

document.addEventListener('keydown', (event) => {
    if (isTyping(event.target)) return;

    switch (event.code) {
        case 'Space': event.preventDefault(); player.toggle(); break;
        case 'ArrowRight': event.preventDefault(); player.next(); break;
        case 'ArrowLeft': event.preventDefault(); player.previous(); break;
        case 'KeyS': event.preventDefault(); player.setShuffle(!store.state.mode.shuffle); break;
        case 'KeyR': event.preventDefault(); player.cycleRepeat(); break;
        case 'KeyM': event.preventDefault(); player.toggleMute(); break;
        default: break;
    }

    // Deliberately not bound to the arrow keys, which is where a full-screen music player would
    // put volume. #view is the document's only scroll region now, and arrows are how a keyboard
    // user scrolls it -- taking them would trade a control that has a slider for one that does
    // not have a replacement.

    // '?' rather than a code, because which physical key produces it depends on the layout.
    if (event.key === '?') {
        event.preventDefault();
        void showShortcuts();
    }
});

/**
 * Whether a key event belongs to the focused control rather than the player.
 *
 * The original guard checked only INPUT and TEXTAREA. That misses contenteditable,
 * select, and -- now that the track list is a listbox -- any widget where Space and the
 * arrow keys already mean something. Stealing arrows from the list would break the
 * keyboard navigation the list exists to provide.
 */
function isTyping(target) {
    if (!(target instanceof Element)) return false;
    if (target.isContentEditable) return true;
    if (['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return true;

    return target.closest('[role="listbox"], [role="slider"], [role="dialog"]') !== null;
}

// ---------------------------------------------------------------------------
// Sync
// ---------------------------------------------------------------------------

async function requestSync() {
    try {
        await api.requestSync();
        announce('Sync requested.');
        pollSyncChip();

        if (store.state.trackCount === 0) router.navigate('/sync');
    } catch (error) {
        alertUser(error.message);
    }
}

let chipTimer = null;

/** Keeps the top-bar chip current while a sync runs, then stops polling. */
function pollSyncChip() {
    clearTimeout(chipTimer);

    chipTimer = setTimeout(async function tick() {
        try {
            const status = await api.syncStatus();
            const running = status.status === 'Running' || status.status === 'Queued' || status.pending;

            dom.syncChip.hidden = !running;
            dom.syncChip.textContent = running
                ? `SYNCING ${status.itemsProcessed}/${status.itemsTotal || '?'}`
                : '';

            store.set({ trackCount: status.trackCount });

            // Stops on its own rather than polling forever in a background tab.
            if (running) chipTimer = setTimeout(tick, 1500);
        } catch {
            dom.syncChip.hidden = true;
        }
    }, 300);
}

function onSyncFinished(trackCount) {
    store.set({ trackCount });
    announce(`Sync complete. ${trackCount} tracks.`);
    router.navigate('/library', { replace: true });
}

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------

handleUnauthenticatedWith(() => {
    // Never a hard redirect: that would destroy playback and the queue. The banner and
    // the welcome view explain the situation and re-authentication is one click.
    if (store.state.session?.authenticated) {
        alertUser('Your session expired. Please sign in again.');
        store.set({ session: { authenticated: false, driveConnected: false, status: 'Pending' } });
        router.refresh();
    }
});

// ---------------------------------------------------------------------------
// Progressive web app
// ---------------------------------------------------------------------------

/**
 * Registers the service worker and offers an update rather than forcing one.
 */
function registerServiceWorker() {
    if (!('serviceWorker' in navigator)) return;

    // Not on localhost.
    //
    // The worker's cache name is a hardcoded version string, so during development it pins the
    // shell indefinitely: every edit to a precached file keeps serving the old copy until the
    // registration is manually cleared, and `fetch(..., { cache: 'reload' })` does not help
    // because the request still goes through the worker. That cost real time three separate
    // times while building this, each presenting as "my change did nothing".
    //
    // Offline behaviour is therefore verified against a deployed instance rather than locally.
    // The alternative -- deriving the cache name from a build stamp -- is the better long-term
    // answer, but it needs the server to template sw.js, and this is the honest version of the
    // trade in the meantime.
    if (['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname)) {
        return;
    }

    // Deferred until the page has loaded, so registration never competes with the first
    // paint. But this is called from boot(), which awaits the session request first, and by
    // then the load event has usually already fired -- so a bare addEventListener('load')
    // silently never runs and the worker is never registered. Check the state as well as
    // listening for the transition.
    if (document.readyState === 'complete') {
        void register();
        return;
    }

    window.addEventListener('load', () => void register(), { once: true });

    async function register() {
        try {
            const registration = await navigator.serviceWorker.register('/sw.js');

            registration.addEventListener('updatefound', () => {
                const incoming = registration.installing;
                if (!incoming) return;

                incoming.addEventListener('statechange', () => {
                    // "installed" with an existing controller means a new version is waiting.
                    if (incoming.state === 'installed' && navigator.serviceWorker.controller) {
                        offerUpdate(registration);
                    }
                });
            });
        } catch {
            // A failed registration must never break the app. It only costs offline support.
        }
    }
}

let reloading = false;

function offerUpdate(registration) {
    // Deliberately not applied automatically. Activating a new worker under a live page can
    // tear down the audio element mid-song, which is not a trade worth making to ship a CSS
    // change a few seconds earlier. A cold start picks it up regardless.
    if (store.state.playing) {
        announce('An update is ready and will be applied when playback stops.');
        return;
    }

    const banner = document.createElement('div');
    banner.className = 'topbar__status';
    banner.textContent = 'NEW BUILD AVAILABLE';

    const reload = document.createElement('button');
    reload.type = 'button';
    reload.className = 'btn btn--sm';
    reload.textContent = 'RELOAD';

    reload.addEventListener('click', () => {
        registration.waiting?.postMessage({ type: 'SKIP_WAITING' });
    });

    dom.topbar.append(banner, reload);
    announce('A new version is available.');

    navigator.serviceWorker.addEventListener('controllerchange', () => {
        // Guarded: controllerchange can fire more than once, and an unguarded reload here
        // is the classic way to build an infinite refresh loop.
        if (reloading) return;
        reloading = true;
        window.location.reload();
    });
}

/**
 * Reflects connectivity in the top bar.
 */
function watchConnectivity() {
    const update = () => {
        const offline = navigator.onLine === false;

        dom.syncChip.hidden = !offline && dom.syncChip.textContent === '';

        if (offline) {
            // navigator.onLine only reports whether a network interface exists, so it says
            // true on a captive portal or a dead uplink. It is reliable in one direction
            // only: false really does mean offline.
            dom.syncChip.hidden = false;
            dom.syncChip.textContent = '⚠ OFFLINE';
            alertUser('You are offline. Playback of already-buffered audio may continue.');
        } else if (dom.syncChip.textContent.includes('OFFLINE')) {
            dom.syncChip.textContent = '';
            dom.syncChip.hidden = true;
        }
    };

    window.addEventListener('online', update);
    window.addEventListener('offline', update);

    update();
}

async function boot() {
    try {
        const session = await api.session();
        store.set({ session });

        // Guarded on isReady, not on `authenticated`: a pending account's /api/sync/status
        // call answers 403, so asking would spend a request to learn nothing and would put
        // a failure in the console on every load for someone whose only problem is waiting.
        if (isReady(session)) {
            const status = await api.syncStatus().catch(() => null);
            if (status) store.set({ trackCount: status.trackCount });
            pollSyncChip();

            // Restores the saved queue. /api/playback returns the queued tracks' metadata as
            // well as their ids, so this needs no library page to have loaded first.
            await persistence.restore();
        }
    } catch {
        store.set({ session: { authenticated: false, driveConnected: false, status: 'Pending' } });
    }

    renderChrome(store.state);

    // Bound after the first render so the initial state does not fire handlers before the
    // transport exists.
    bindMediaSession({ player, store, audio: dom.audio });

    watchConnectivity();
    registerServiceWorker();

    await router.start();
}

boot();
