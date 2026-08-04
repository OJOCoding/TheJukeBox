// Checks that the service worker's precache list covers every module the app actually loads.
//
//   node --test tests/js/precache.test.mjs
//
// The list is hand-maintained, so it drifts silently: a new view file is imported, nothing
// adds it to PRECACHE, and everything keeps working online. The failure only appears offline,
// where the import 404s against the cache and the whole shell fails to boot -- which is the
// one situation in which nobody is watching a console.
//
// /js/ui/virtual-list.js had already gone missing this way before this test existed.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, posix } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const wwwroot = join(root, 'src', 'TheJukeBox', 'wwwroot');

const sw = readFileSync(join(wwwroot, 'sw.js'), 'utf8');

/** The PRECACHE array's string entries. */
const precached = new Set(
    [...(sw.match(/const PRECACHE = \[([\s\S]*?)\n\];/)?.[1] ?? '').matchAll(/'([^']+)'/g)]
        .map((m) => m[1]),
);

/**
 * Every module reachable from main.js, followed transitively.
 *
 * Walked rather than globbed: a file that exists but is imported by nothing does not need
 * precaching, and demanding it would push people to keep dead entries in the list.
 */
function reachableModules(entry) {
    const found = new Set();
    const queue = [entry];

    while (queue.length > 0) {
        const url = queue.pop();

        if (found.has(url)) continue;
        found.add(url);

        const source = readFileSync(join(wwwroot, url.replace(/^\//, '')), 'utf8');

        // Only relative specifiers. A bare specifier would be a bundler import, and this app
        // deliberately has no build step, so there are none to resolve.
        for (const match of source.matchAll(/(?:^|\s)(?:import|export)[^'"]*from\s+'(\.[^']+)'/g)) {
            queue.push(posix.normalize(posix.join(posix.dirname(url), match[1])));
        }
    }

    return found;
}

describe('the service worker precache list', () => {
    it('was parsed at all', () => {
        // Guards the guard: a regex that stops matching would make every assertion below
        // pass against an empty set.
        assert.ok(precached.size > 5, `only found ${precached.size} entries in PRECACHE`);
    });

    it('covers every module reachable from main.js', () => {
        const modules = [...reachableModules('/js/main.js')].sort();

        assert.ok(modules.length > 5, `only walked ${modules.length} modules, so this proves little`);

        const missing = modules.filter((url) => !precached.has(url));

        assert.deepEqual(
            missing,
            [],
            `the service worker does not precache: ${missing.join(', ')}. `
            + 'Offline, these 404 against the cache and the shell fails to boot.',
        );
    });

    it('does not precache files that do not exist', () => {
        // addAll is atomic, so one bad entry fails the whole install and the app silently
        // loses offline support entirely.
        const broken = [...precached]
            .filter((url) => url !== '/' && !url.includes('?'))
            .filter((url) => {
                try {
                    readFileSync(join(wwwroot, url.replace(/^\//, '')));
                    return false;
                } catch {
                    return true;
                }
            });

        assert.deepEqual(broken, [], `PRECACHE lists missing files: ${broken.join(', ')}`);
    });

    it('has a version that is bumped when the list changes', () => {
        // Not verifiable statically -- this only asserts the mechanism exists, so that the
        // comment explaining the obligation cannot be deleted without a test failing.
        assert.match(sw, /const VERSION = '/, 'sw.js should carry an explicit cache version');
        assert.match(sw, /jb-shell-\$\{VERSION\}/, 'the shell cache name should include the version');
    });
});
