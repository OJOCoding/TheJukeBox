// Tests for the play queue.
//
// Run with:  node --test tests/js
//
// Node's built-in test runner, so this needs no packages and no build step, which keeps
// the frontend's "no npm dependencies" constraint intact.
//
// The queue is worth testing precisely because its predecessor's bug was not a typo but
// a modelling error: play order was stored as a second array of indices into a mutable
// playlist, so any library change silently invalidated it. These assert the invariants
// that make that impossible now, rather than the incidental behaviour of one code path.

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { createQueue } from '../../src/TheJukeBox/wwwroot/js/player/queue.js';

const ids = (n) => Array.from({ length: n }, (_, i) => `t${i}`);

/** Walks the queue forward until it stops, returning what it played. */
function playThrough(queue, limit = 100) {
    const played = [queue.currentId];

    for (let i = 0; i < limit; i++) {
        const next = queue.advance(1);
        if (next === null) break;
        played.push(next);
    }

    return played;
}

describe('linear playback', () => {
    it('plays every track once, in order', () => {
        const queue = createQueue();
        queue.replace(ids(5));

        assert.deepEqual(playThrough(queue), ['t0', 't1', 't2', 't3', 't4']);
    });

    it('ends rather than wrapping when repeat is off', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 2 });

        // The old playNext used modulo arithmetic, so the last track always wrapped to
        // the first and a queue could never finish even with repeat off.
        assert.equal(queue.peek(1), null);
        assert.equal(queue.advance(1), null);
        assert.equal(queue.currentId, 't2', 'a refused advance must not move the cursor');
    });

    it('has nowhere to go back to from the first track', () => {
        const queue = createQueue();
        queue.replace(ids(3));

        assert.equal(queue.peek(-1), null);
    });

    it('starts where asked', () => {
        const queue = createQueue();
        queue.replace(ids(5), { startAt: 3 });

        assert.equal(queue.currentId, 't3');
    });

    it('clamps an out-of-range start instead of ending up empty', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 99 });

        assert.equal(queue.currentId, 't2');
    });
});

describe('repeat', () => {
    it('wraps to the beginning when set to all', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 2 });
        queue.setRepeat('all');

        assert.equal(queue.advance(1), 't0');
    });

    it('returns the same track when set to one, without moving', () => {
        const queue = createQueue();
        queue.replace(ids(3), { startAt: 1 });
        queue.setRepeat('one');

        assert.equal(queue.peek(1), 't1');
        assert.equal(queue.advance(1), 't1');
        assert.equal(queue.currentId, 't1');
    });

    it('cycles off, all, one and back', () => {
        const queue = createQueue();

        assert.equal(queue.cycleRepeat(), 'all');
        assert.equal(queue.cycleRepeat(), 'one');
        assert.equal(queue.cycleRepeat(), 'off');
    });
});

describe('shuffle', () => {
    it('plays every track exactly once', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(50));

        const played = playThrough(queue);

        // A permutation, not a resampling. This is what a naive
        // sort(() => Math.random() - 0.5) fails to guarantee.
        assert.equal(played.length, 50);
        assert.deepEqual([...played].sort(), ids(50).sort());
    });

    it('still starts on the track the user clicked', () => {
        for (let attempt = 0; attempt < 25; attempt++) {
            const queue = createQueue();
            queue.setShuffle(true);
            queue.replace(ids(20), { startAt: 7 });

            assert.equal(queue.currentId, 't7', 'the requested track must play first');
        }
    });

    it('does not interrupt the current track when toggled on', () => {
        const queue = createQueue();
        queue.replace(ids(20), { startAt: 5 });

        queue.setShuffle(true);

        assert.equal(queue.currentId, 't5');
    });

    it('does not interrupt the current track when toggled off', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(20), { startAt: 5 });

        queue.setShuffle(false);

        assert.equal(queue.currentId, 't5');

        // And linear order resumes from there rather than from wherever the shuffle was.
        assert.equal(queue.advance(1), 't6');
    });

    it('is idempotent', () => {
        const queue = createQueue();
        queue.replace(ids(10), { startAt: 4 });
        queue.setShuffle(true);

        const after = queue.currentId;
        queue.setShuffle(true);

        assert.equal(queue.currentId, after, 'setting the same mode must not reshuffle');
    });
});

describe('reconcile after a library sync', () => {
    it('keeps shuffle on', () => {
        const queue = createQueue();
        queue.setShuffle(true);
        queue.replace(ids(10));

        queue.reconcile(new Set(ids(10)));

        // This is the bug that motivated the redesign. loadMusicLibrary used to reset the
        // shuffled order to sequential without consulting isShuffle, so the button kept
        // reading "on" while playback ran in order. Mode and order cannot disagree here
        // because the order is derived from the mode.
        assert.equal(queue.mode.shuffle, true);
    });

    it('keeps playing what is playing', () => {
        const queue = createQueue();
        queue.replace(ids(10), { startAt: 6 });

        queue.reconcile(new Set(ids(10)));

        assert.equal(queue.currentId, 't6');
    });

    it('keeps playing what is playing even in shuffle', () => {
        for (let attempt = 0; attempt < 25; attempt++) {
            const queue = createQueue();
            queue.setShuffle(true);
            queue.replace(ids(20), { startAt: 11 });

            queue.reconcile(new Set(ids(20)));

            assert.equal(queue.currentId, 't11');
        }
    });

    it('drops tracks that no longer exist', () => {
        const queue = createQueue();
        queue.replace(ids(5));

        queue.reconcile(new Set(['t0', 't2', 't4']));

        assert.equal(queue.length, 3);
        assert.deepEqual(queue.ids, ['t0', 't2', 't4']);
    });

    it('survives the playing track being deleted', () => {
        const queue = createQueue();
        queue.replace(ids(5), { startAt: 2 });

        queue.reconcile(new Set(['t0', 't1', 't3', 't4']));

        // Whatever it lands on, it must be a real queued track rather than a stale index
        // pointing past the end.
        assert.equal(queue.length, 4);
        assert.ok(queue.currentId === null || queue.ids.includes(queue.currentId));
    });

    it('survives every track being deleted', () => {
        const queue = createQueue();
        queue.replace(ids(5), { startAt: 2 });

        queue.reconcile(new Set());

        assert.equal(queue.length, 0);
        assert.equal(queue.currentId, null);
        assert.equal(queue.peek(1), null);
    });
});

describe('degenerate cases', () => {
    it('an empty queue has nothing to play', () => {
        const queue = createQueue();
        queue.replace([]);

        assert.equal(queue.currentId, null);
        assert.equal(queue.peek(1), null);
        assert.equal(queue.advance(1), null);
    });

    it('a single track does not repeat itself when repeat is off', () => {
        const queue = createQueue();
        queue.replace(['only']);

        assert.equal(queue.currentId, 'only');
        assert.equal(queue.advance(1), null);
    });

    it('a single track repeats when repeat is all', () => {
        const queue = createQueue();
        queue.replace(['only']);
        queue.setRepeat('all');

        assert.equal(queue.advance(1), 'only');
    });

    it('jumpTo moves to a queued track and refuses an unknown one', () => {
        const queue = createQueue();
        queue.replace(ids(5));

        assert.equal(queue.jumpTo('t3'), true);
        assert.equal(queue.currentId, 't3');

        assert.equal(queue.jumpTo('nope'), false);
        assert.equal(queue.currentId, 't3', 'a refused jump must not move the cursor');
    });
});
