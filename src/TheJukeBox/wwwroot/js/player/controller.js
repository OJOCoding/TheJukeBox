// Owns the one <audio> element and the queue.
//
// The element lives in index.html, outside the router's outlet, and is never recreated.
// That is what makes playback survive navigation. It also matters on iOS, where an
// element created after the user's gesture may silently refuse to play because it has no
// gesture history of its own.

import { alertUser, announce } from '../core/ui.js';
import { api } from '../core/api.js';
import { createQueue } from './queue.js';

export function createPlayer({ audio, store }) {
    const queue = createQueue();

    /** Tracks by id, so the transport can label what is playing. */
    let catalogue = new Map();

    /** Ids that failed this session, so a broken file is skipped rather than retried forever. */
    const unplayable = new Set();

    let consecutiveFailures = 0;
    let retried = new Set();
    let seeking = false;

    function trackOf(id) {
        return id === null ? null : catalogue.get(id) ?? null;
    }

    function publish() {
        const id = queue.currentId;

        store.set({
            currentTrackId: id,
            currentTrack: trackOf(id),
            mode: queue.mode,
            hasNext: queue.peek(1) !== null,
            hasPrevious: queue.peek(-1) !== null,
        });
    }

    function load(trackId, { autoplay = true } = {}) {
        const track = trackOf(trackId);
        if (!track) return;

        audio.src = api.streamUrl(trackId);
        audio.load();

        publish();
        announce(`Now playing: ${track.title}${track.artist ? `, ${track.artist}` : ''}.`);

        if (autoplay) play();
    }

    function play() {
        const attempt = audio.play();

        // Older engines return undefined rather than a promise.
        if (attempt?.catch) {
            attempt.catch((error) => {
                // AbortError is our own src change interrupting a pending play, not a
                // failure worth showing.
                if (error.name === 'AbortError' || error.name === 'NotAllowedError') return;
                alertUser(`Cannot play: ${error.message}`);
            });
        }
    }

    audio.addEventListener('playing', () => {
        consecutiveFailures = 0;
        store.set({ playing: true, buffering: false });
    });

    audio.addEventListener('pause', () => store.set({ playing: false }));

    audio.addEventListener('waiting', () => store.set({ buffering: true }));

    audio.addEventListener('loadedmetadata', () =>
        store.set({ duration: Number.isFinite(audio.duration) ? audio.duration : 0 }));

    audio.addEventListener('timeupdate', () => {
        // Suppressed while the user drags the seek bar, or the thumb fights them.
        if (!seeking) store.set({ position: audio.currentTime });
    });

    audio.addEventListener('ended', () => {
        if (queue.mode.repeat === 'one') {
            audio.currentTime = 0;
            play();
            return;
        }

        const next = queue.advance(1);

        if (next === null) {
            store.set({ playing: false });
            announce('End of queue.');
            return;
        }

        load(next);
    });

    audio.addEventListener('error', () => {
        const id = queue.currentId;

        // MEDIA_ERR_ABORTED is our own src change. The old code needed a
        // `hasUserSelectedTrack` flag to suppress spurious errors because it assigned
        // src = '' and called load(); clearing with removeAttribute avoids the cause, so
        // every remaining error is real.
        if (audio.error?.code === MediaError.MEDIA_ERR_ABORTED) return;
        if (!id) return;

        if (!retried.has(id)) {
            retried.add(id);
            setTimeout(() => { if (queue.currentId === id) load(id); }, 800);
            return;
        }

        unplayable.add(id);
        const track = trackOf(id);
        alertUser(`Cannot play ${track?.title ?? 'this track'}. Skipping.`);

        // A queue of broken files must not spin forever.
        if (++consecutiveFailures >= 3) {
            store.set({ playing: false });
            alertUser('Several tracks in a row failed. Stopping.');
            return;
        }

        const next = queue.advance(1);
        if (next === null) store.set({ playing: false });
        else load(next);
    });

    return {
        /** Replaces what the player knows about, and drops queued ids that vanished. */
        setCatalogue(tracks) {
            catalogue = new Map(tracks.map((track) => [track.id, track]));
            queue.reconcile(new Set(catalogue.keys()));
            publish();
        },

        /** Plays `trackId`, queueing `ids` around it. */
        playTrack(trackId, ids) {
            const list = ids ?? [...catalogue.keys()];
            queue.replace(list, { startAt: Math.max(list.indexOf(trackId), 0) });

            retried = new Set();
            consecutiveFailures = 0;

            load(trackId);
        },

        toggle() {
            if (!audio.src) {
                alertUser('Select a track first.');
                return;
            }

            if (audio.paused) play();
            else audio.pause();
        },

        next() {
            const id = queue.advance(1);
            if (id === null) {
                announce('End of queue.');
                return;
            }
            load(id);
        },

        previous() {
            // Restart rather than skip back when the track is already underway, which is
            // what every music player does and what users expect.
            if (audio.currentTime > 3) {
                audio.currentTime = 0;
                return;
            }

            const id = queue.advance(-1);
            if (id === null) {
                audio.currentTime = 0;
                return;
            }
            load(id);
        },

        seekTo(seconds) {
            if (Number.isFinite(audio.duration)) {
                audio.currentTime = Math.min(Math.max(seconds, 0), audio.duration);
            }
        },

        beginSeek() { seeking = true; },

        endSeek(seconds) {
            seeking = false;
            this.seekTo(seconds);
        },

        setShuffle(on) {
            queue.setShuffle(on);
            publish();
            announce(on ? 'Shuffle on.' : 'Shuffle off.');
        },

        cycleRepeat() {
            const mode = queue.cycleRepeat();
            publish();
            announce(mode === 'off' ? 'Repeat off.' : mode === 'all' ? 'Repeat all.' : 'Repeat one.');
            return mode;
        },

        isUnplayable: (id) => unplayable.has(id),
    };
}
