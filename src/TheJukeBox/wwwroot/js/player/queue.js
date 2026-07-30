// The play queue.
//
// The old model stored a second array of *indices into a mutable playlist*
// (`shuffledPlaylist`), which is the root of the shuffle bug: any change to the library
// silently invalidated it. loadMusicLibrary reset it to sequential order without
// consulting isShuffle, so after a refresh the button still read "on" while playback ran
// in order; and toggleShuffle only rebuilt it inside an `if (shuffleBtn)` guard, so the
// model depended on a DOM node existing.
//
// Here the queue holds stable track ids and the play order is a *derived* permutation,
// recomputed from a single mode flag. There is no second array to fall out of sync and
// no DOM anywhere near it, so that class of bug is designed out rather than patched.

/** @returns {object} a queue over stable track ids. */
export function createQueue() {
    /** @type {string[]} track ids, in the order they were added. */
    let items = [];

    /** @type {number[]} a permutation of positions in `items`. */
    let order = [];

    /** Index into `order`, not into `items`. */
    let cursor = -1;

    let shuffle = false;

    /** 'off' | 'all' | 'one' */
    let repeat = 'off';

    const positionOf = () => (cursor < 0 ? -1 : order[cursor]);

    /** Rebuilds the permutation, optionally keeping the playing track under the cursor. */
    function reorder({ keepCurrent = true } = {}) {
        const keep = keepCurrent ? positionOf() : -1;

        order = [...items.keys()];

        if (!shuffle) {
            // Linear: the cursor and the position are the same thing.
            cursor = keep >= 0 ? keep : Math.min(cursor, order.length - 1);
            return;
        }

        // Fisher-Yates. Preserved from the original, which was correct -- unlike the
        // common sort(() => Math.random() - 0.5), which is biased and not a shuffle.
        for (let i = order.length - 1; i > 0; i--) {
            const j = Math.floor(Math.random() * (i + 1));
            [order[i], order[j]] = [order[j], order[i]];
        }

        if (keep >= 0) {
            // Keep playing what is playing: move it to the front and sit on it, so
            // toggling shuffle never interrupts the current track.
            const at = order.indexOf(keep);
            [order[0], order[at]] = [order[at], order[0]];
            cursor = 0;
        }
    }

    return {
        get length() {
            return items.length;
        },

        get currentId() {
            const position = positionOf();
            return position < 0 ? null : items[position];
        },

        get mode() {
            return { shuffle, repeat };
        },

        get ids() {
            return [...items];
        },

        /** Replaces the queue, optionally starting at a position in the supplied order. */
        replace(ids, { startAt = 0 } = {}) {
            items = [...ids];
            cursor = -1;

            reorder({ keepCurrent: false });

            if (items.length === 0) return;

            const start = Math.min(Math.max(startAt, 0), items.length - 1);

            // In shuffle, the requested track still plays first: the user asked for it.
            cursor = shuffle ? order.indexOf(start) : start;

            if (shuffle && cursor > 0) {
                [order[0], order[cursor]] = [order[cursor], order[0]];
                cursor = 0;
            }
        },

        /** One place sets shuffle, and it has no DOM dependency. */
        setShuffle(on) {
            if (shuffle === on) return;
            shuffle = on;
            reorder();
        },

        setRepeat(mode) {
            repeat = mode;
        },

        /** Cycles off -> all -> one -> off, and returns the new mode. */
        cycleRepeat() {
            repeat = repeat === 'off' ? 'all' : repeat === 'all' ? 'one' : 'off';
            return repeat;
        },

        /**
         * The id `delta` steps away, or null at the end.
         *
         * Returning null is the fix for playback never ending: the old playNext used
         * modulo arithmetic, so the last track always wrapped to the first and the queue
         * could not finish even with repeat off.
         */
        peek(delta) {
            if (items.length === 0) return null;
            if (repeat === 'one' && delta > 0) return items[positionOf()] ?? null;

            const next = cursor + delta;

            if (next >= 0 && next < order.length) return items[order[next]];
            if (repeat === 'all') return items[order[(next + order.length) % order.length]];

            return null;
        },

        /** Moves `delta` steps and returns the new id, or null if there is nowhere to go. */
        advance(delta) {
            const next = this.peek(delta);
            if (next === null) return null;

            // Repeat-one stays put; the controller restarts the same track.
            if (repeat === 'one' && delta > 0) return next;

            cursor = (cursor + delta + order.length) % order.length;
            return next;
        },

        /** Jumps to a specific id if it is queued. */
        jumpTo(trackId) {
            const position = items.indexOf(trackId);
            if (position < 0) return false;

            const at = order.indexOf(position);
            if (at < 0) return false;

            cursor = at;
            return true;
        },

        /**
         * Drops ids that no longer exist, preserving what is playing.
         *
         * Called after a library sync. Shuffle mode survives because the mode flag is
         * never touched here -- the permutation is derived from it, so there is nothing
         * to keep in step.
         */
        reconcile(validIds) {
            const playing = this.currentId;

            items = items.filter((id) => validIds.has(id));

            reorder({ keepCurrent: false });

            const position = playing === null ? -1 : items.indexOf(playing);

            if (position >= 0) {
                cursor = shuffle ? order.indexOf(position) : position;
            } else {
                cursor = Math.min(cursor, order.length - 1);
            }
        },
    };
}
