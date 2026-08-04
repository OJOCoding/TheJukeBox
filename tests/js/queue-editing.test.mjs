// Tests for the queue operations C2 added: append, remove, clear, snapshot and restore.
//
//   node --test tests/js/queue-editing.test.mjs
//
// These are the operations a visible queue needs, and they are all ways to mutate `items`
// underneath a live cursor. The invariant they must not break is the one the queue exists to
// protect: the cursor keeps pointing at the track that is playing, whatever happens around it.

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { createQueue } from '../../src/TheJukeBox/wwwroot/js/player/queue.js';

const ids = (n) => Array.from({ length: n }, (_, i) => `t${i}`);

describe('append', () => {
    it('adds to the end without disturbing the current track', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 1 });

        queue.append(['x', 'y']);

        assert.equal(queue.currentId, 't1');
        assert.deepEqual(queue.ids, ['t0', 't1', 't2', 'x', 'y']);
        assert.equal(queue.length, 5);
    });

    it('makes newly added tracks reachable', () => {
        const queue = createQueue();
        queue.replace(['a']);

        queue.append(['b']);

        assert.equal(queue.advance(1), 'b');
    });

    it('appending nothing changes nothing', () => {
        const queue = createQueue();
        queue.replace(ids(2));

        queue.append([]);

        assert.deepEqual(queue.ids, ids(2));
        assert.equal(queue.currentId, 't0');
    });

    it('appends into an empty queue', () => {
        const queue = createQueue();

        queue.append(['a', 'b']);

        assert.deepEqual(queue.ids, ['a', 'b']);
        assert.equal(queue.length, 2);
    });

    /**
     * Under shuffle the additions have to land in the unplayed remainder. Appending them to the
     * end of the order instead would play them in insertion order after everything else, which
     * is exactly what shuffle is supposed to prevent.
     */
    it('interleaves additions into the unplayed remainder when shuffling', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(4));

        const playing = queue.currentId;

        queue.append(['x', 'y', 'z']);

        assert.equal(queue.currentId, playing, 'appending must not change what is playing');

        // Everything still queued must be reachable by advancing.
        const played = [playing];
        let next;
        while ((next = queue.advance(1)) !== null) played.push(next);

        assert.equal(played.length, 7);
        assert.deepEqual([...played].sort(), [...ids(4), 'x', 'y', 'z'].sort());
    });
});

describe('removeAt', () => {
    it('removes an entry after the cursor and leaves the cursor alone', () => {
        const queue = createQueue();
        queue.replace(ids(4), { startAt: 1 });

        assert.equal(queue.removeAt(3), false, 'that entry was not the one playing');

        assert.equal(queue.currentId, 't1');
        assert.deepEqual(queue.ids, ['t0', 't1', 't2']);
    });

    /**
     * The case positional arithmetic gets wrong. Removing an entry before the cursor shifts
     * every later position down by one, so a cursor left untouched would point at the wrong
     * track -- silently, and only for users who prune their queue.
     */
    it('removing an entry before the cursor keeps the same track playing', () => {
        const queue = createQueue();
        queue.replace(ids(5), { startAt: 3 });

        assert.equal(queue.currentId, 't3');

        assert.equal(queue.removeAt(0), false);

        assert.equal(queue.currentId, 't3', 'the cursor must follow its track, not its index');
        assert.deepEqual(queue.ids, ['t1', 't2', 't3', 't4']);
        assert.equal(queue.advance(1), 't4');
    });

    it('reports when the removed entry was the one playing', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 1 });

        assert.equal(queue.removeAt(1), true);
        assert.deepEqual(queue.ids, ['t0', 't2']);
    });

    /**
     * Removing what is playing must leave the queue positioned so the next track is the one
     * that took its place, not the start of the queue again.
     */
    it('continues from the gap after removing the playing entry', () => {
        const queue = createQueue();
        queue.replace(ids(4), { startAt: 1 });

        queue.removeAt(1);

        assert.equal(queue.advance(1), 't3', 't2 moved into the gap, so next is the one after it');
    });

    it('removing the last entry empties the queue', () => {
        const queue = createQueue();
        queue.replace(['only']);

        assert.equal(queue.removeAt(0), true);

        assert.equal(queue.length, 0);
        assert.equal(queue.currentId, null);
        assert.equal(queue.advance(1), null);
    });

    it('removes the correct copy when a track is queued twice', () => {
        const queue = createQueue();
        queue.replace(['a', 'b', 'a'], { startAt: 2 });

        assert.equal(queue.currentId, 'a');

        // The first copy, not the one playing.
        assert.equal(queue.removeAt(0), false);

        assert.deepEqual(queue.ids, ['b', 'a']);
        assert.equal(queue.currentId, 'a');
        assert.equal(queue.position, 1, 'the playing copy is the second one');
    });

    it('refuses an out-of-range position', () => {
        const queue = createQueue();
        queue.replace(ids(2));

        assert.equal(queue.removeAt(-1), false);
        assert.equal(queue.removeAt(9), false);
        assert.deepEqual(queue.ids, ids(2));
    });

    it('keeps the playing track when shuffling', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(6), { startAt: 2 });

        const playing = queue.currentId;
        const doomed = queue.ids.findIndex((id) => id !== playing);

        queue.removeAt(doomed);

        assert.equal(queue.currentId, playing);
        assert.equal(queue.length, 5);
    });
});

describe('clear', () => {
    it('empties the queue and uncues everything', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 1 });

        queue.clear();

        assert.equal(queue.length, 0);
        assert.equal(queue.currentId, null);
        assert.equal(queue.position, -1);
        assert.deepEqual(queue.ids, []);
        assert.equal(queue.peek(1), null);
    });

    it('leaves the mode alone', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.setRepeat('all');
        queue.replace(ids(3));

        queue.clear();

        // Clearing the queue is not a request to change how playback behaves.
        assert.deepEqual(queue.mode, { shuffle: true, repeat: 'all' });
    });
});

describe('upcoming', () => {
    it('lists the current track first, then what follows', () => {
        const queue = createQueue();
        queue.replace(ids(4), { startAt: 1 });

        assert.deepEqual(queue.upcoming(), ['t1', 't2', 't3']);
    });

    it('is empty when nothing is cued', () => {
        const queue = createQueue();

        assert.deepEqual(queue.upcoming(), []);
    });

    /**
     * In shuffle mode this must follow the play order, not insertion order. A "coming up next"
     * list built from `ids` would show a sequence that never actually happens.
     */
    it('follows the shuffled play order', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(8), { startAt: 0 });

        const upcoming = queue.upcoming();
        const played = [queue.currentId];

        let next;
        while ((next = queue.advance(1)) !== null) played.push(next);

        assert.deepEqual(upcoming, played, 'upcoming must predict the actual play order');
    });
});

describe('snapshot and restore', () => {
    it('round-trips a linear queue', () => {
        const queue = createQueue();
        queue.replace(ids(5), { startAt: 2 });
        queue.setRepeat('all');

        const saved = queue.snapshot();

        const restored = createQueue();
        restored.restore(saved);

        assert.deepEqual(restored.ids, ids(5));
        assert.equal(restored.currentId, 't2');
        assert.equal(restored.position, 2);
        assert.deepEqual(restored.mode, { shuffle: false, repeat: 'all' });
        assert.equal(restored.advance(1), 't3');
    });

    /**
     * The shuffled order comes from an unseeded Fisher-Yates, so it cannot be restored
     * faithfully. What must survive is the track that was playing -- resuming a different track
     * is the visible failure; a different upcoming order is not.
     */
    it('resumes the same track when the saved queue was shuffled', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(10), { startAt: 4 });

        const playing = queue.currentId;
        const saved = queue.snapshot();

        assert.equal(saved.shuffle, true);

        const restored = createQueue();
        restored.restore(saved);

        assert.equal(restored.currentId, playing);
        assert.equal(restored.mode.shuffle, true);
        assert.equal(restored.length, 10);
    });

    it('restores a queue with nothing cued', () => {
        const queue = createQueue();

        queue.restore({ ids: ids(3), cursor: -1 });

        assert.equal(queue.length, 3);
        assert.equal(queue.currentId, null);
        assert.equal(queue.position, -1);

        // Advancing from uncued starts at the beginning rather than doing nothing.
        assert.equal(queue.advance(1), 't0');
    });

    it('restores an empty snapshot', () => {
        const queue = createQueue();
        queue.replace(ids(3));

        queue.restore({ ids: [], cursor: -1 });

        assert.equal(queue.length, 0);
        assert.equal(queue.currentId, null);
    });

    it('restores from nothing at all', () => {
        const queue = createQueue();

        queue.restore();

        assert.equal(queue.length, 0);
        assert.equal(queue.currentId, null);
        assert.deepEqual(queue.mode, { shuffle: false, repeat: 'off' });
    });

    /**
     * The saved state comes back over HTTP, so it is untrusted input. A cursor past the end must
     * not leave the queue pointing at a track that is not there.
     */
    it('clamps a cursor that is outside the saved queue', () => {
        const queue = createQueue();

        queue.restore({ ids: ids(3), cursor: 99 });

        assert.equal(queue.currentId, null);
        assert.equal(queue.position, -1);
        assert.equal(queue.length, 3);
    });

    it('rejects a repeat mode it does not recognise', () => {
        const queue = createQueue();

        queue.restore({ ids: ids(2), cursor: 0, repeat: 'sometimes' });

        assert.equal(queue.mode.repeat, 'off');
    });

    /**
     * The server stores repeat as a .NET enum and sends its name -- "All", not "all". A
     * case-sensitive check silently reset every restored queue to repeat-off, which nothing
     * reported because the value was valid, just wrong.
     */
    it('accepts the repeat mode in the casing the server sends', () => {
        for (const [sent, expected] of [['Off', 'off'], ['All', 'all'], ['One', 'one'], ['ALL', 'all']]) {
            const queue = createQueue();

            queue.restore({ ids: ids(2), cursor: 0, repeat: sent });

            assert.equal(queue.mode.repeat, expected, `"${sent}" should restore as "${expected}"`);
        }
    });

    it('survives a repeat mode that is not a string', () => {
        const queue = createQueue();

        queue.restore({ ids: ids(2), cursor: 0, repeat: 1 });

        assert.equal(queue.mode.repeat, 'off');
    });

    it('treats a non-boolean shuffle flag as off', () => {
        const queue = createQueue();

        queue.restore({ ids: ids(2), cursor: 0, shuffle: 'yes' });

        assert.equal(queue.mode.shuffle, false);
    });

    it('a snapshot of an empty queue reports nothing cued', () => {
        const saved = createQueue().snapshot();

        assert.deepEqual(saved.ids, []);
        assert.equal(saved.cursor, -1);
    });

    /** The snapshot must be a copy, or a later mutation would edit what was already saved. */
    it('a snapshot does not alias the live queue', () => {
        const queue = createQueue();
        queue.replace(ids(3));

        const saved = queue.snapshot();
        queue.append(['x']);

        assert.deepEqual(saved.ids, ids(3));
    });
});
