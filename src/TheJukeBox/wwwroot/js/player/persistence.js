// Keeps the server's copy of the queue in step with the live one.
//
// This is what makes closing the tab and coming back land you where you were, and what makes a
// queue built on a laptop show up on a phone. Without it the queue lives only in one page's
// memory, which is where it lived before C2.
//
// Two different rhythms, deliberately:
//
//   - Structural changes (a track added, removed, skipped, shuffle toggled) are saved shortly
//     after they happen, debounced so a burst of skips is one request.
//   - The playback offset changes continuously, so it is saved on a slow timer while playing
//     and once when the page goes away. Saving it on every timeupdate would be four writes a
//     second per listener, for a value nobody misses being a few seconds stale.

import { api } from '../core/api.js';

/** How long to wait after a structural change before saving. */
const DEBOUNCE_MS = 1500;

/**
 * The longest a save may be deferred by further changes.
 *
 * A plain debounce can be starved: every change pushes the timer out, so a run of them means the
 * save never happens. That is not hypothetical here -- a queue of unplayable files produces a
 * skip every few hundred milliseconds as the error handler works through them, which is exactly
 * the situation where losing the queue state matters most.
 */
const MAX_DEFER_MS = 8000;

/** How often to save the playback offset while audio is playing. */
const HEARTBEAT_MS = 15000;

export function createPersistence({ player, store }) {
    let debounceTimer = null;
    let heartbeatTimer = null;
    let stopped = false;

    /** When the currently pending save was first requested, for the max-defer cap. */
    let pendingSince = 0;

    /** Set once the initial restore has finished, so restoring does not immediately re-save. */
    let live = false;

    function payload() {
        const snapshot = player.snapshot();

        return {
            trackIds: snapshot.ids,
            cursor: snapshot.cursor,
            positionSeconds: snapshot.positionSeconds,
            shuffle: snapshot.shuffle,
            repeat: snapshot.repeat,
        };
    }

    async function save({ keepalive = false } = {}) {
        if (!live || stopped) return;

        clearTimeout(debounceTimer);
        debounceTimer = null;
        pendingSince = 0;

        try {
            await api.savePlaybackState(payload(), { keepalive });
        } catch {
            // A failed save costs the resume point, nothing more. Surfacing it would interrupt
            // listening to report something the user cannot act on.
        }
    }

    function scheduleSave() {
        const now = Date.now();

        if (pendingSince === 0) pendingSince = now;

        // Capped, so a stream of changes cannot defer the save forever.
        const wait = Math.min(DEBOUNCE_MS, Math.max(0, pendingSince + MAX_DEFER_MS - now));

        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => void save(), wait);
    }

    /**
     * Restores the saved queue, if there is one.
     *
     * Returns whether anything was restored, so the caller can decide where to send the user.
     */
    async function restore() {
        try {
            const state = await api.playbackState();

            if (!state || state.trackIds.length === 0) return false;

            // Before restoring: the player resolves queued ids through its catalogue, and at boot
            // that catalogue is empty. Without this the whole queue is dropped as unknown.
            player.mergeCatalogue(state.tracks ?? []);

            player.restore({
                ids: state.trackIds,
                cursor: state.cursor,
                shuffle: state.shuffle,
                repeat: state.repeat,
                positionSeconds: state.positionSeconds,
            });

            return state.cursor >= 0;
        } catch {
            // Never fatal. A missing or unreadable resume point means starting fresh, which is
            // the state every first-time user is in anyway.
            return false;
        } finally {
            // Set even on failure, so later changes are still saved.
            live = true;
        }
    }

    // Structural changes come from the player itself rather than from watching the store: the
    // store also ticks on every position update, and telling those apart after the fact is
    // guesswork.
    player.onQueueChange(scheduleSave);

    const unsubscribe = store.subscribe((state) => {
        // The heartbeat only runs while audio is actually playing. A paused tab left open
        // overnight should not write to the database every fifteen seconds.
        if (state.playing && heartbeatTimer === null) {
            heartbeatTimer = setInterval(() => void save(), HEARTBEAT_MS);
        } else if (!state.playing && heartbeatTimer !== null) {
            clearInterval(heartbeatTimer);
            heartbeatTimer = null;

            // Pausing is a good moment to record where we stopped.
            scheduleSave();
        }
    });

    // pagehide, not beforeunload or unload: it is the only one of the three that fires reliably
    // when a mobile browser backgrounds a tab, which on a phone is how listening usually ends.
    const onHide = () => {
        clearTimeout(debounceTimer);

        // keepalive, or the request is cancelled with the document and the last position is lost.
        void save({ keepalive: true });

        // Any listen in progress is written now too, for the same reason.
        player.flush();
    };

    window.addEventListener('pagehide', onHide);

    return {
        restore,

        /** Saves immediately. For the moments where waiting out the debounce would be wrong. */
        flush: () => save(),

        dispose() {
            stopped = true;

            clearTimeout(debounceTimer);
            if (heartbeatTimer !== null) clearInterval(heartbeatTimer);

            unsubscribe();
            window.removeEventListener('pagehide', onHide);
        },
    };
}
