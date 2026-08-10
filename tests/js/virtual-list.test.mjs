// Tests for the virtual list's windowing arithmetic.
//
//   node --test tests/js/virtual-list.test.mjs
//
// Only the pure function is covered here: createVirtualList needs a DOM, and the parts of it
// that could be wrong without a DOM are exactly the index calculations below. Rendering the
// wrong window shows blank space or the wrong rows, and neither is obvious from reading the
// code -- but both are trivially assertable.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

import { VIRTUALISE_ABOVE, computeWindow } from '../../src/TheJukeBox/wwwroot/js/ui/virtual-list.js';

const wwwroot = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TheJukeBox', 'wwwroot');

const ROW = 56;

/** 10 rows visible in a 560px viewport, over a 1000-track library. */
const base = { viewportHeight: 560, rowHeight: ROW, total: 1000, overscan: 6 };

describe('computeWindow', () => {
    it('starts at the top of the list', () => {
        const { first, last } = computeWindow({ ...base, scrollOffset: 0 });

        // Clamped at zero rather than running negative into the overscan.
        assert.equal(first, 0);

        // 10 visible + 1 partial + 6 overscan.
        assert.equal(last, 17);
    });

    it('treats a negative offset as the top', () => {
        // The list sits below the viewport top when a header is above it and the user has not
        // scrolled down yet. A negative index window would render nothing.
        const { first, last } = computeWindow({ ...base, scrollOffset: -400 });

        assert.equal(first, 0);
        assert.ok(last > 0);
    });

    it('moves the window as the list scrolls', () => {
        const { first, last } = computeWindow({ ...base, scrollOffset: 100 * ROW });

        assert.equal(first, 94);   // 100 - 6 overscan
        assert.equal(last, 117);   // 100 + 11 + 6
    });

    it('renders a partially scrolled row', () => {
        // Half a row up: the row at index 10 is still partly visible, so it must be included.
        const { first } = computeWindow({ ...base, scrollOffset: (10 * ROW) + (ROW / 2) });

        assert.equal(first, 4);    // floor(10.5) - 6
    });

    it('clamps to the final row at the bottom', () => {
        const { first, last } = computeWindow({ ...base, scrollOffset: 1000 * ROW });

        assert.equal(last, 999, 'must never index past the end');
        assert.ok(first <= 999);
    });

    it('never returns a window outside the list', () => {
        for (const offset of [-10_000, -1, 0, 1, ROW - 1, 500 * ROW, 999 * ROW, 10_000 * ROW]) {
            const { first, last } = computeWindow({ ...base, scrollOffset: offset });

            assert.ok(first >= 0, `first ${first} below zero at offset ${offset}`);
            assert.ok(last <= base.total - 1, `last ${last} past the end at offset ${offset}`);
            assert.ok(first <= last + 1, `inverted window at offset ${offset}`);
        }
    });

    it('covers every row of a short list in one window', () => {
        const { first, last } = computeWindow({ ...base, total: 5, scrollOffset: 0 });

        assert.equal(first, 0);
        assert.equal(last, 4);
    });

    it('reports an empty window for an empty list', () => {
        const { first, last } = computeWindow({ ...base, total: 0, scrollOffset: 0 });

        // last < first is the empty signal, so the caller's inclusive loop runs zero times.
        assert.equal(last, -1);
        assert.ok(last < first);
    });

    it('reports an empty window rather than dividing by zero', () => {
        const { last } = computeWindow({ ...base, rowHeight: 0, scrollOffset: 100 });

        assert.equal(last, -1);
    });

    /**
     * The point of the whole exercise. A window over a large library must stay small,
     * otherwise virtualisation is costing complexity for nothing.
     */
    it('keeps the window small regardless of library size', () => {
        for (const total of [1_000, 10_000, 250_000]) {
            const { first, last } = computeWindow({ ...base, total, scrollOffset: 500 * ROW });
            const size = last - first + 1;

            assert.ok(size <= 30, `window of ${size} rows for ${total} tracks`);
        }
    });

    it('overscans on both sides so scrolling does not reveal blank rows', () => {
        const offset = 100 * ROW;

        const none = computeWindow({ ...base, scrollOffset: offset, overscan: 0 });
        const some = computeWindow({ ...base, scrollOffset: offset, overscan: 6 });

        assert.equal(none.first - some.first, 6);
        assert.equal(some.last - none.last, 6);
    });

    it('grows the window with the viewport', () => {
        const short = computeWindow({ ...base, viewportHeight: 200, scrollOffset: 0 });
        const tall = computeWindow({ ...base, viewportHeight: 2000, scrollOffset: 0 });

        assert.ok(tall.last > short.last, 'a taller viewport must render more rows');
    });
});

describe('the virtualisation threshold', () => {
    it('is high enough that ordinary libraries render plainly', () => {
        // Virtualisation is the riskier path for assistive technology (NVDA browse-mode
        // buffers go stale after DOM recycling), so most users should never take it.
        assert.ok(VIRTUALISE_ABOVE >= 500);
    });

    it('is low enough to engage before rendering becomes slow', () => {
        assert.ok(VIRTUALISE_ABOVE <= 2000);
    });
});

/**
 * The row heights are written down twice: once as a CSS custom property that sizes the row, and
 * once as a JavaScript constant that positions it. They are not derived from each other, because
 * reading a custom property means a laid-out element and the renderer runs before there is one.
 *
 * So they can drift, and drift here is invisible in the small case and silently broken in the
 * large one: below the threshold nothing is positioned arithmetically and everything looks fine,
 * and above it every row lands a few pixels further from where it is drawn than the last, until
 * rows overlap or leave gaps hundreds of rows down. Nobody has a library that long until they do.
 */
describe('the row heights the windowed lists position by', () => {
    const tokens = readFileSync(join(wwwroot, 'css', 'tokens.css'), 'utf8');

    /** The pixel value of a `--name: 56px;` declaration. */
    function token(name) {
        const match = tokens.match(new RegExp(`--${name}:\\s*(\\d+)px`));

        assert.ok(match, `tokens.css declares no --${name}`);

        return Number(match[1]);
    }

    /** The `const ROW_HEIGHT = 56;` a view positions its rows with. */
    function constantIn(...path) {
        const source = readFileSync(join(wwwroot, ...path), 'utf8');
        const match = source.match(/const ROW_HEIGHT = (\d+);/);

        assert.ok(match, `${path.join('/')} declares no ROW_HEIGHT`);

        return Number(match[1]);
    }

    it('agree with --row-h for the library', () => {
        assert.equal(constantIn('js', 'views', 'library.js'), token('row-h'));
    });

    it('agree with --queue-row-h for the queue', () => {
        assert.equal(constantIn('js', 'views', 'queue.js'), token('queue-row-h'));
    });

    it('agree with --queue-row-h for playlists', () => {
        assert.equal(constantIn('js', 'views', 'playlists.js'), token('queue-row-h'));
    });

    it('clear the tallest row each list can produce', () => {
        // Measured in a browser rather than derived: a library row is one line of text and a
        // queue row stacks a title over an artist, which came out at 67.1px. A value under the
        // natural height clips, because the windowed rule applies it exactly.
        assert.ok(token('row-h') >= 56, 'a one-line library row measured 56px');
        assert.ok(token('queue-row-h') >= 68, 'a two-line queue row measured 67.1px');
    });
});
