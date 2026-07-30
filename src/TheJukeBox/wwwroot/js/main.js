// Application entry point.

import { alertUser, announce, mmss, spoken } from './core/ui.js';
import { api, handleUnauthenticatedWith } from './core/api.js';
import { createRouter } from './core/router.js';
import { createStore } from './core/store.js';
import { createPlayer } from './player/controller.js';
import { renderLibrary } from './views/library.js';
import { renderSyncProgress, renderWelcome } from './views/welcome.js';

const dom = {
    view: document.getElementById('view'),
    audio: document.getElementById('audio'),
    topbar: document.getElementById('topbar'),
    transport: document.getElementById('transport'),
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
    syncButton: document.getElementById('syncButton'),
    syncChip: document.getElementById('syncChip'),
    signOutButton: document.getElementById('signOutButton'),
};

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
});

const player = createPlayer({ audio: dom.audio, store });

// ---------------------------------------------------------------------------
// Routing
// ---------------------------------------------------------------------------

const routes = [
    { path: '/', title: 'Welcome', render: (outlet) => renderWelcome(outlet, { store, onSync: requestSync }) },
    { path: '/library', title: 'Library', render: (outlet) => renderLibrary(outlet, { store, player, onSync: requestSync }) },
    { path: '/sync', title: 'Initial sync', render: (outlet) => renderSyncProgress(outlet, { onDone: onSyncFinished }) },
];

const router = createRouter({
    outlet: dom.view,
    routes,
    resolve(pathname, route) {
        const session = store.state.session;

        // Until the bootstrap call lands, render nothing rather than flashing the
        // signed-out view at someone who is signed in.
        if (session === null) return { route: null };

        const ready = session.authenticated && session.driveConnected;

        // Signed out, or no Drive grant: only the welcome view makes sense.
        if (!ready) return pathname === '/' ? { route: routes[0] } : { redirectTo: '/' };

        // An unrecognised path lands on the library. This is also what catches bookmarks
        // to the retired /player page.
        if (!route) return { redirectTo: '/library' };

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

dom.syncButton.addEventListener('click', requestSync);

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
    const signedIn = state.session?.authenticated === true && state.session?.driveConnected === true;

    dom.topbar.hidden = !signedIn;
    dom.transport.hidden = !signedIn;

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

    dom.shuffleButton.setAttribute('aria-pressed', state.mode.shuffle ? 'true' : 'false');

    const repeat = state.mode.repeat;
    dom.repeatButton.dataset.state = repeat;
    dom.repeatButton.textContent = repeat === 'off' ? 'REPEAT' : repeat === 'all' ? 'REPEAT ALL' : 'REPEAT ONE';
    dom.repeatButton.setAttribute(
        'aria-label',
        `Repeat: ${repeat}. Activate for repeat ${repeat === 'off' ? 'all' : repeat === 'all' ? 'one' : 'off'}.`,
    );

    dom.duration.textContent = mmss(state.duration);
    dom.seek.max = String(Math.floor(state.duration || 0));

    // Left alone while dragging, so the thumb does not fight the user.
    if (document.activeElement !== dom.seek) {
        dom.seek.value = String(Math.floor(state.position || 0));
        dom.position.textContent = mmss(state.position);
    }
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
        default: break;
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
        store.set({ session: { authenticated: false, driveConnected: false } });
        router.refresh();
    }
});

async function boot() {
    try {
        const session = await api.session();
        store.set({ session });

        if (session.authenticated && session.driveConnected) {
            const status = await api.syncStatus().catch(() => null);
            if (status) store.set({ trackCount: status.trackCount });
            pollSyncChip();
        }
    } catch {
        store.set({ session: { authenticated: false, driveConnected: false } });
    }

    renderChrome(store.state);
    await router.start();
}

boot();
