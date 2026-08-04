// Route pattern matching.
//
//   node --test tests/js/router-match.test.mjs
//
// The router gained `:param` segments for /playlists/:id. Matching is the one part of a router
// that is worth testing in isolation: it decides which view runs, so a wrong match is either a
// blank screen or -- worse -- a view rendered for a URL it was not written for.
//
// The matcher is not exported (it closes over the route table), so this reimplements nothing:
// it drives createRouter with a fake DOM and observes which route rendered.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'node:test';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const routerSource = readFileSync(
    join(root, 'src', 'TheJukeBox', 'wwwroot', 'js', 'core', 'router.js'),
    'utf8',
);

/**
 * Loads router.js with its `announce` import stubbed and a minimal DOM in place.
 *
 * Importing it for real would need a document, a History API and a live announcer element.
 * Rewriting the one import and evaluating the module is less machinery than a DOM shim, and it
 * runs the shipped source rather than a copy of it.
 */
async function loadRouter() {
    const calls = [];
    const source = routerSource.replace(
        /import \{ announce \} from '\.\/ui\.js';/,
        'const announce = (m) => globalThis.__announced.push(m);',
    );

    globalThis.__announced = [];

    // A document stand-in: only the pieces the router touches.
    globalThis.document = {
        title: '',
        querySelectorAll: () => [],
        addEventListener: () => {},
    };

    globalThis.window = {
        location: { pathname: '/', origin: 'http://localhost' },
        history: {
            replaceState: (_state, _title, path) => { globalThis.window.location.pathname = path; },
            pushState: (_state, _title, path) => { globalThis.window.location.pathname = path; },
        },
        addEventListener: () => {},
    };

    const module = await import(
        `data:text/javascript,${encodeURIComponent(source)}`
    );

    return { createRouter: module.createRouter, calls };
}

/**
 * A router over a fixed table, recording what rendered with which params.
 *
 * `resolve` receives the wrapped route objects as a third argument. A guard has to hand back a
 * route the router can actually render, so a test that substitutes one must substitute a real
 * entry rather than a lookalike.
 */
async function routerFor(routes, resolve) {
    const { createRouter } = await loadRouter();
    const rendered = [];

    const outlet = {
        replaceChildren: () => {},
        focus: () => {},
        scrollTop: 0,
    };

    const wrapped = routes.map((route) => ({
        ...route,
        render: (_outlet, params) => {
            rendered.push({ path: route.path, params });
            return null;
        },
    }));

    const router = createRouter({
        outlet,
        routes: wrapped,
        resolve: resolve
            ? (pathname, route) => resolve(pathname, route, wrapped)
            : (pathname, route) => ({ route }),
    });

    return { router, rendered, routes: wrapped };
}

const TABLE = [
    { path: '/', title: 'Welcome' },
    { path: '/library', title: 'Library' },
    { path: '/queue', title: 'Queue' },
    { path: '/playlists', title: 'Playlists' },
    { path: '/playlists/new', title: 'New playlist' },
    { path: '/playlists/:id', title: 'Playlist' },
];

describe('route matching', () => {
    it('matches a static route with no params', async () => {
        const { router, rendered } = await routerFor(TABLE);

        await router.navigate('/library');

        assert.deepEqual(rendered, [{ path: '/library', params: {} }]);
    });

    it('extracts a single named parameter', async () => {
        const { router, rendered } = await routerFor(TABLE);

        await router.navigate('/playlists/0193f7c2-1111-7000-8000-000000000000');

        assert.deepEqual(rendered, [{
            path: '/playlists/:id',
            params: { id: '0193f7c2-1111-7000-8000-000000000000' },
        }]);
    });

    /**
     * Order-independence matters: /playlists/new is declared before /playlists/:id here, but a
     * reader should not have to know that for the code to be correct.
     */
    it('prefers a static route over a pattern that would also match', async () => {
        const { router, rendered } = await routerFor([...TABLE].reverse());

        await router.navigate('/playlists/new');

        assert.deepEqual(rendered, [{ path: '/playlists/new', params: {} }]);
    });

    it('does not match a pattern against a different segment count', async () => {
        const { router, rendered } = await routerFor(TABLE);

        await router.navigate('/playlists/abc/extra');

        assert.equal(rendered.length, 0, 'a three-segment path must not match a two-segment pattern');
    });

    /** An empty id would sail through to the API and 404 for no visible reason. */
    it('does not match an empty parameter segment', async () => {
        const { router, rendered } = await routerFor(TABLE);

        await router.navigate('/playlists/');

        assert.equal(rendered.length, 0);
    });

    it('decodes a percent-encoded parameter', async () => {
        const { router, rendered } = await routerFor(TABLE);

        await router.navigate('/playlists/a%20b');

        assert.deepEqual(rendered[0].params, { id: 'a b' });
    });

    /**
     * A regex-based matcher built by interpolation is the classic way to get this wrong: a path
     * full of regex metacharacters must be compared literally, not compiled.
     */
    it('treats regex metacharacters in a path as literal text', async () => {
        const { router, rendered } = await routerFor([
            { path: '/', title: 'Welcome' },
            { path: '/a.c', title: 'Dotted' },
        ]);

        await router.navigate('/abc');

        assert.equal(rendered.length, 0, '"/a.c" must not match "/abc"');

        await router.navigate('/a.c');

        assert.deepEqual(rendered, [{ path: '/a.c', params: {} }]);
    });

    /**
     * When a guard substitutes a different route, the params belong to a URL the user is no
     * longer on. Passing them through would hand the welcome view a playlist id.
     */
    it('does not leak params to a route the guard substituted', async () => {
        const { router, rendered } = await routerFor(
            [{ path: '/', title: 'Welcome' }, { path: '/playlists/:id', title: 'Playlist' }],

            // The guard sends every request to the welcome view, as the signed-out guard does.
            (_pathname, _route, wrapped) => ({ route: wrapped[0] }),
        );

        await router.navigate('/playlists/secret-id');

        assert.deepEqual(rendered, [{ path: '/', params: {} }]);
    });
});
