// Checks that main.js and index.html agree about which elements exist.
//
//   node --test tests/js/dom-contract.test.mjs
//
// This exists because of a real, total outage. C3 added a renderArt() that read `dom.nowArt`,
// but the corresponding `nowArt: document.getElementById('nowArt')` entry was never added to
// the map. `dom.nowArt` was therefore undefined, the first property access threw inside
// renderChrome, boot() rejected before the router ever started, and every visitor got a blank
// page. Nothing in the console unless you were already listening.
//
// It survived review and it survived manual checking, because every check that did not require
// boot() to *finish* still passed: the markup was correct, the stylesheet was correct, the
// service worker registered when registered by hand. A static check on the contract between
// the two files costs nothing and catches it before a browser is involved.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const wwwroot = join(root, 'src', 'TheJukeBox', 'wwwroot');

const html = readFileSync(join(wwwroot, 'index.html'), 'utf8');
const mainJs = readFileSync(join(wwwroot, 'js', 'main.js'), 'utf8');

/** Every id declared in the markup. */
const declaredIds = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]));

/** Every id main.js looks up. */
const lookedUpIds = [...mainJs.matchAll(/getElementById\(\s*'([^']+)'\s*\)/g)].map((m) => m[1]);

/** The keys of the `dom` object literal. */
const domKeys = new Set(
    [...(mainJs.match(/const dom = \{([\s\S]*?)\n\};/)?.[1] ?? '').matchAll(/^\s*([a-zA-Z][\w]*)\s*:/gm)]
        .map((m) => m[1]),
);

/** Every `dom.something` referenced anywhere in main.js. */
const domUses = new Set([...mainJs.matchAll(/\bdom\.([a-zA-Z][\w]*)/g)].map((m) => m[1]));

describe('the dom map', () => {
    it('was parsed at all', () => {
        // Guards the guard. If these regexes stop matching, every assertion below passes
        // vacuously and the test becomes decoration.
        assert.ok(declaredIds.size > 5, `only found ${declaredIds.size} ids in index.html`);
        assert.ok(domKeys.size > 5, `only found ${domKeys.size} keys in the dom map`);
        assert.ok(domUses.size > 5, `only found ${domUses.size} dom.* uses`);
    });

    it('only looks up ids that the markup declares', () => {
        const missing = lookedUpIds.filter((id) => !declaredIds.has(id));

        assert.deepEqual(missing, [], `main.js looks up ids absent from index.html: ${missing}`);
    });

    /** The exact failure that took the app down. */
    it('defines an entry for every dom.* reference', () => {
        const undefinedUses = [...domUses].filter((name) => !domKeys.has(name)).sort();

        assert.deepEqual(
            undefinedUses,
            [],
            `main.js reads dom.${undefinedUses.join(', dom.')} but the map has no such key, `
            + 'so it is undefined at runtime and the first property access throws',
        );
    });

    it('has no entry nothing uses', () => {
        // Not a crash, but a stale entry means a control was removed and its wiring left
        // behind -- worth knowing before it accumulates.
        const unused = [...domKeys].filter((name) => !domUses.has(name)).sort();

        assert.deepEqual(unused, [], `the dom map defines unused entries: ${unused}`);
    });

    it('fails loudly at startup if the markup and the map drift', () => {
        // The runtime guard is the second line of defence, for a mismatch this static check
        // cannot see (an id renamed only in the markup at deploy time, say).
        assert.match(mainJs, /missingElements/, 'main.js should verify its dom map at startup');
        assert.match(mainJs, /throw new Error/, 'a missing element should throw, not warn');
    });
});

describe('the templates main.js and its views clone', () => {
    it('exist in the markup', () => {
        const templateIds = new Set(
            [...html.matchAll(/<template\s+id="([^"]+)"/g)].map((m) => m[1]),
        );

        const libraryJs = readFileSync(join(wwwroot, 'js', 'views', 'library.js'), 'utf8');

        const cloned = [...libraryJs.matchAll(/fromTemplate\(\s*'([^']+)'\s*\)/g)].map((m) => m[1]);

        assert.ok(cloned.length > 0, 'no fromTemplate calls found, so this proves nothing');

        for (const id of cloned) {
            assert.ok(templateIds.has(id), `library.js clones missing template "${id}"`);
        }
    });
});

describe('the hidden attribute', () => {
    /**
     * `hidden` has to actually hide.
     *
     * The browser's `[hidden] { display: none }` lives in the user-agent stylesheet, so any author
     * `display` rule outranks it -- author beats UA regardless of specificity. This app toggles
     * `hidden` from JavaScript on the top bar, the transport, the account menu and the avatar, and
     * every one of those carries an author `display`. So none of them hid: the track bar showed on
     * the landing page, the account menu was permanently open, and a signed-out visitor saw the nav
     * and the sync button.
     *
     * Nothing in the DOM would reveal this to a test that only checks the `hidden` property -- the
     * property was correct the whole time. The rule below is the fix, and this is the guard.
     */
    it('is defeated by no author display rule', () => {
        const base = readFileSync(join(wwwroot, 'css', 'base.css'), 'utf8');

        // Whitespace-tolerant: the point is that the declaration exists and is !important.
        const rule = /\[hidden\]\s*\{[^}]*display\s*:\s*none\s*!important/;

        assert.match(
            base,
            rule,
            'base.css must contain [hidden] { display: none !important }. Without it, any component '
            + 'that sets its own display ignores the hidden attribute entirely.',
        );
    });

    /**
     * And the elements that rely on it are worth naming, so that removing the rule fails with a
     * list of what breaks rather than an abstract complaint.
     */
    it('is what the shell relies on to hide its chrome', () => {
        const toggled = [...html.matchAll(/<[^>]*\bid="([^"]+)"[^>]*\bhidden\b/g)].map((m) => m[1]);

        assert.ok(
            toggled.length >= 3,
            `expected several elements to ship hidden; found ${toggled.join(', ') || 'none'}`,
        );

        // These three are the ones whose visibility is a correctness question rather than a
        // cosmetic one: playback controls with nothing to control, and a menu that opens itself.
        for (const id of ['topbar', 'transport', 'profileMenu']) {
            assert.ok(
                toggled.includes(id),
                `#${id} should ship with the hidden attribute and be revealed by JavaScript`,
            );
        }
    });
});
