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

    /**
     * The listen in progress, flushed to history when it ends.
     *
     * Accumulated from playback rather than read off `currentTime`, so seeking forward through a
     * track does not report time that was never heard.
     */
    let listen = null;

    /** Called whenever the queue's contents or cursor change, so the state can be saved. */
    let onQueueChanged = () => {};

    function trackOf(id) {
        return id === null ? null : catalogue.get(id) ?? null;
    }

    function publish({ queueChanged = false } = {}) {
        const id = queue.currentId;

        store.set({
            currentTrackId: id,
            currentTrack: trackOf(id),
            mode: queue.mode,
            hasNext: queue.peek(1) !== null,
            hasPrevious: queue.peek(-1) !== null,
            queueLength: queue.length,

            // A counter rather than the queue itself. Views re-render when it changes, and
            // putting a 2000-element array in the store would make every position tick compare
            // it against the previous one.
            queueRevision: (store.state.queueRevision ?? 0) + 1,
        });

        if (queueChanged) onQueueChanged();
    }

    /**
     * Records the listen that just finished.
     *
     * One row per listen, written when it ends rather than when it crosses the threshold. The
     * alternative -- reporting the moment 20 seconds elapse -- has to guess at `Completed`
     * before the track has finished, so a fully played track would be logged as a partial one.
     */
    function flushListen() {
        const finished = listen;
        listen = null;

        if (!finished) return;

        const seconds = Math.floor(finished.seconds);

        // The server enforces this too. Checking here as well avoids a request per skip.
        if (seconds < 20 && !finished.completed) return;

        // Fire and forget: a failed history write must never interrupt playback, and there is
        // nothing useful to tell the user about one.
        void api.recordPlay({
            trackId: finished.trackId,
            secondsPlayed: seconds,
            completed: finished.completed,
        }).catch(() => {});
    }

    function load(trackId, { autoplay = true, resumeAt = 0 } = {}) {
        const track = trackOf(trackId);
        if (!track) return;

        // Before the src changes: whatever was playing has now finished being listened to.
        flushListen();

        listen = { trackId, seconds: 0, completed: false, lastTime: 0 };

        audio.src = api.streamUrl(trackId);
        audio.load();

        if (resumeAt > 0) {
            // currentTime cannot be set until the browser knows the duration, so this waits for
            // metadata rather than assigning and hoping.
            audio.addEventListener('loadedmetadata', function seek() {
                audio.removeEventListener('loadedmetadata', seek);

                if (Number.isFinite(audio.duration) && resumeAt < audio.duration) {
                    audio.currentTime = resumeAt;
                    listen.lastTime = resumeAt;
                }
            });
        }

        publish({ queueChanged: true });
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

        if (listen) {
            // Only small forward deltas count as heard audio. A jump is a seek, and counting it
            // would let dragging to the end of a track report it as fully listened to.
            const delta = audio.currentTime - listen.lastTime;

            if (delta > 0 && delta <= 2) listen.seconds += delta;

            listen.lastTime = audio.currentTime;
        }
    });

    audio.addEventListener('ended', () => {
        if (listen) listen.completed = true;

        if (queue.mode.repeat === 'one') {
            // Counted as a separate listen each time round, which is what a repeat is.
            flushListen();
            listen = { trackId: queue.currentId, seconds: 0, completed: false, lastTime: 0 };

            audio.currentTime = 0;
            play();
            return;
        }

        const next = queue.advance(1);

        if (next === null) {
            flushListen();
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
        /**
         * Adds tracks the player can name and stream.
         *
         * Additive, and it does not prune the queue. Both of those are deliberate, and both
         * replace earlier behaviour that was wrong:
         *
         * The catalogue used to be *replaced* by whatever the library view had just loaded, and
         * the queue reconciled against it. But that list is one filtered, paged slice of the
         * library, not an inventory of it -- so typing in the search box pruned every queued
         * track that did not match the search. The queue was ephemeral before C2, which is
         * probably why nobody noticed; persisting it would have made the loss permanent.
         *
         * Tracks that really have gone are handled where the evidence is unambiguous instead: the
         * resume path filters the saved queue against the library server-side, and a file that
         * disappeared mid-session fails to stream and is skipped by the error handler.
         */
        mergeCatalogue(tracks) {
            for (const track of tracks) {
                catalogue.set(track.id, track);
            }

            // Republished so the transport picks up metadata that arrived after the track was
            // cued -- a restored queue labels itself this way.
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

        /** Notified whenever the queue's contents, cursor or mode change. */
        onQueueChange(callback) {
            onQueueChanged = typeof callback === 'function' ? callback : () => {};
        },

        /**
         * The whole queue in insertion order, with positions.
         *
         * Positions are indices into the queue's own list, which is what `removeAt` and
         * `jumpToPosition` take. Returning display indices instead would make every caller
         * convert, and a duplicated track would make that conversion ambiguous.
         */
        queueEntries() {
            const playing = queue.position;

            return queue.ids.map((id, position) => ({
                position,
                trackId: id,
                track: trackOf(id),
                isCurrent: position === playing,
            }));
        },

        /** Adds to the end of the queue without interrupting what is playing. */
        enqueue(ids) {
            const known = ids.filter((id) => catalogue.has(id));
            if (known.length === 0) return 0;

            queue.append(known);
            publish({ queueChanged: true });

            announce(`${known.length} track${known.length === 1 ? '' : 's'} added to the queue.`);

            return known.length;
        },

        /** Plays `ids` from the start, replacing the queue. */
        playAll(ids, { startAt = 0 } = {}) {
            const known = ids.filter((id) => catalogue.has(id));

            if (known.length === 0) {
                alertUser('Nothing here can be played.');
                return false;
            }

            queue.replace(known, { startAt: Math.min(Math.max(startAt, 0), known.length - 1) });

            retried = new Set();
            consecutiveFailures = 0;

            load(queue.currentId);

            return true;
        },

        /** Jumps to a queued entry by position, so a duplicated track is unambiguous. */
        jumpToPosition(position) {
            const id = queue.ids[position];
            if (id === undefined) return;

            // jumpTo finds the first copy, which for a duplicated track is the wrong one. Setting
            // it by position is why the queue exposes both.
            if (queue.ids.indexOf(id) === position) queue.jumpTo(id);
            else queue.replace(queue.ids, { startAt: position });

            load(id);
        },

        /** Removes one queued entry, continuing playback if it was the one playing. */
        removeAt(position) {
            const wasPlaying = queue.removeAt(position);

            if (!wasPlaying) {
                publish({ queueChanged: true });
                return;
            }

            const next = queue.currentId ?? queue.peek(1);

            if (next === null || next === undefined) {
                audio.pause();

                // removeAttribute rather than src = '': assigning an empty string makes the
                // browser try to load the current document as audio and fire a real error.
                audio.removeAttribute('src');
                audio.load();

                store.set({ playing: false, position: 0, duration: 0 });
                publish({ queueChanged: true });
                return;
            }

            load(next);
        },

        clearQueue() {
            flushListen();

            queue.clear();

            audio.pause();
            audio.removeAttribute('src');
            audio.load();

            store.set({ playing: false, position: 0, duration: 0 });
            publish({ queueChanged: true });

            announce('Queue cleared.');
        },

        /** Everything needed to restore this queue later, plus where in the track we are. */
        snapshot() {
            return {
                ...queue.snapshot(),
                positionSeconds: Number.isFinite(audio.currentTime) ? audio.currentTime : 0,
            };
        },

        /**
         * Restores a saved queue without starting playback.
         *
         * Deliberately not autoplaying. Audio that begins on its own when a page loads is
         * hostile, and on iOS it is refused anyway because there has been no user gesture.
         */
        restore(state) {
            queue.restore(state);

            const id = queue.currentId;

            if (id !== null && catalogue.has(id)) {
                load(id, { autoplay: false, resumeAt: state.positionSeconds ?? 0 });
            } else {
                publish();
            }
        },

        /** Writes any listen in progress. Called before the page goes away. */
        flush() {
            flushListen();
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

            // Mode is part of the saved state, so changing it is a change worth persisting.
            publish({ queueChanged: true });
            announce(on ? 'Shuffle on.' : 'Shuffle off.');
        },

        cycleRepeat() {
            const mode = queue.cycleRepeat();
            publish({ queueChanged: true });
            announce(mode === 'off' ? 'Repeat off.' : mode === 'all' ? 'Repeat all.' : 'Repeat one.');
            return mode;
        },

        isUnplayable: (id) => unplayable.has(id),
    };
}
