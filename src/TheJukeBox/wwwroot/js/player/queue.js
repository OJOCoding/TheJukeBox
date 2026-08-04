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

        /** The position of the playing track within `ids`, or -1. */
        get position() {
            return positionOf();
        },

        /**
         * The ids in the order they will actually play, from the current track onwards.
         *
         * Derived from `order`, not from `items`: in shuffle mode those differ, and a "coming
         * up next" list built from insertion order would show a sequence that never happens.
         */
        upcoming() {
            if (cursor < 0) return [];

            return order.slice(cursor).map((position) => items[position]);
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

        /** Appends ids to the end, keeping the current track and cursor. */
        append(ids) {
            if (ids.length === 0) return;

            const playing = positionOf();

            items = [...items, ...ids];

            if (!shuffle) {
                // Linear: the new positions go on the end and the cursor is unchanged, so there
                // is nothing to rebuild.
                order = [...items.keys()];
                return;
            }

            // Shuffled: the additions are interleaved into the part of the order that has not
            // played yet. Appending them to the end instead would make "add to queue" during
            // shuffle play them in insertion order, which is not what shuffle means.
            const added = [...items.keys()].slice(items.length - ids.length);

            for (const position of added) {
                const at = cursor < 0
                    ? Math.floor(Math.random() * (order.length + 1))
                    : cursor + 1 + Math.floor(Math.random() * (order.length - cursor));

                order.splice(at, 0, position);
            }

            // The cursor indexes `order`, and an insert before it would shift what is playing.
            if (playing >= 0) cursor = order.indexOf(playing);
        },

        /**
         * Removes one entry by its position in `ids`.
         *
         * Positional rather than by id, because the same track may be queued twice and
         * "remove this one" has to be unambiguous.
         *
         * @returns {boolean} whether the removed entry was the one playing.
         */
        removeAt(position) {
            if (position < 0 || position >= items.length) return false;

            const playing = positionOf();
            const wasPlaying = playing === position;

            items.splice(position, 1);

            // Rebuilt rather than patched: every position above the hole shifts by one, and
            // adjusting `order` in place is the kind of arithmetic that goes wrong silently.
            const keep = wasPlaying ? -1 : playing > position ? playing - 1 : playing;

            order = [...items.keys()];

            if (shuffle) {
                for (let i = order.length - 1; i > 0; i--) {
                    const j = Math.floor(Math.random() * (i + 1));
                    [order[i], order[j]] = [order[j], order[i]];
                }
            }

            if (keep >= 0) {
                const at = order.indexOf(keep);

                if (shuffle) {
                    // Keep playing what is playing, as reorder() does.
                    [order[0], order[at]] = [order[at], order[0]];
                    cursor = 0;
                } else {
                    cursor = at;
                }
            } else {
                // The playing entry went away, or nothing was playing. Sit where it was so the
                // next `advance(1)` continues from there rather than restarting the queue.
                cursor = Math.min(position, order.length - 1);
            }

            return wasPlaying;
        },

        clear() {
            items = [];
            order = [];
            cursor = -1;
        },

        /**
         * Everything needed to rebuild this queue later.
         *
         * The cursor is a position in `ids`, not an index into the shuffled order. The order is
         * derived from a seedless shuffle, so it cannot be restored faithfully anyway -- and
         * saving an index into something unreproducible would resume the wrong track.
         */
        snapshot() {
            return {
                ids: [...items],
                cursor: positionOf(),
                shuffle,
                repeat,
            };
        },

        /**
         * Rebuilds from a snapshot.
         *
         * Mode is applied before the items, so `replace` builds the right kind of order and
         * puts the saved track under the cursor in one step.
         */
        restore({ ids = [], cursor: at = -1, shuffle: wantShuffle = false, repeat: wantRepeat = 'off' } = {}) {
            shuffle = wantShuffle === true;

            // Lowercased before validating. The server stores this as a .NET enum and sends its
            // name -- "All", not "all" -- so a case-sensitive check silently reset every restored
            // queue to repeat-off. Nothing failed visibly: the mode was just quietly wrong.
            const normalised = typeof wantRepeat === 'string' ? wantRepeat.toLowerCase() : '';

            repeat = ['off', 'all', 'one'].includes(normalised) ? normalised : 'off';

            items = [];
            order = [];
            cursor = -1;

            if (ids.length === 0) return;

            if (at < 0 || at >= ids.length) {
                // A queue with nothing cued: restored, but not pointing anywhere. Advancing
                // from here starts at the beginning.
                items = [...ids];
                reorder({ keepCurrent: false });
                cursor = -1;
                return;
            }

            this.replace(ids, { startAt: at });
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
