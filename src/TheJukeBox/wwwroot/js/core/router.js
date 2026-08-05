// History API routing.
//
// Real URLs, so back and forward work, a view can be linked to, and a refresh lands
// where the user was. The previous app navigated between two documents with a full page
// load, which is precisely why playback stopped whenever the user moved.
//
// The server serves index.html for any non-API path (MapFallbackToFile), so a deep link
// reaches this router rather than 404ing.

import { announce } from './ui.js';

/**
 * @param {object} options
 * @param {HTMLElement} options.outlet Element whose contents are replaced per route.
 * @param {Array<{path: string, title: string, render: Function}>} options.routes
 * @param {Function} options.resolve Maps a pathname to a route, allowing auth guards.
 */
export function createRouter({ outlet, routes, resolve, onNavigated = () => {} }) {
    let current = null;
    let disposeView = null;

    /**
     * Matches a pathname to a route, extracting `:name` segments.
     *
     * Segment-wise rather than by regex. A pattern built by interpolating into a `RegExp` has to
     * escape whatever the path contains, and getting that wrong on a router is how a crafted URL
     * ends up matching a route it should not.
     *
     * Static routes are tried first, so `/playlists/new` can exist alongside `/playlists/:id`
     * without depending on declaration order.
     */
    function match(pathname) {
        const exact = routes.find((route) => route.path === pathname);
        if (exact) return { route: exact, params: {} };

        const wanted = pathname.split('/');

        for (const route of routes) {
            if (!route.path.includes(':')) continue;

            const parts = route.path.split('/');
            if (parts.length !== wanted.length) continue;

            const params = {};
            let ok = true;

            for (let i = 0; i < parts.length; i++) {
                if (parts[i].startsWith(':')) {
                    // An empty segment would make `/playlists/` match `/playlists/:id` with an
                    // empty id, which then 404s from the API for no visible reason.
                    if (wanted[i] === '') { ok = false; break; }
                    params[parts[i].slice(1)] = decodeURIComponent(wanted[i]);
                } else if (parts[i] !== wanted[i]) {
                    ok = false;
                    break;
                }
            }

            if (ok) return { route, params };
        }

        return null;
    }

    function routeFor(pathname) {
        return match(pathname)?.route ?? null;
    }

    async function render(pathname, { announceChange = true } = {}) {
        const matched = match(pathname);
        const target = resolve(pathname, matched?.route ?? null);

        // A guard may send the user elsewhere -- signed out, or no library yet. Replace
        // rather than push, so Back does not bounce them straight back into the view
        // they were just redirected out of.
        if (target.redirectTo && target.redirectTo !== pathname) {
            window.history.replaceState({}, '', target.redirectTo);
            return render(target.redirectTo, { announceChange });
        }

        const route = target.route;
        if (!route) return;

        // Views may hold timers, aborts or listeners. Disposing before swapping is what
        // stops an old view's in-flight fetch from writing into the new one.
        if (typeof disposeView === 'function') {
            disposeView();
            disposeView = null;
        }

        current = route;
        document.title = `${route.title} — JUKEBOX`;

        outlet.replaceChildren();

        // Params come from the match for this pathname, not from the resolved route: a guard may
        // have redirected, in which case the earlier match describes a URL we are no longer on.
        disposeView = (await route.render(outlet, matched?.route === route ? matched.params : {})) ?? null;

        markActive(pathname);

        // Focus moves to the view so a keyboard user is not left on a nav link that no
        // longer describes where they are.
        outlet.focus({ preventScroll: true });
        outlet.scrollTop = 0;

        // After the view exists, so anything the callback shows or hides is reacting to the page
        // the user is actually on rather than the one they were on.
        onNavigated(pathname, route);

        if (announceChange) announce(route.title);
    }

    function markActive(pathname) {
        for (const link of document.querySelectorAll('[data-route]')) {
            const target = link.dataset.route;

            // A section's link stays current on its detail pages: /playlists/<id> should keep
            // PLAYLISTS highlighted, or the user loses their sense of where they are. Exact match
            // for "/", which is a prefix of everything.
            const isActive = target === pathname
                || (target !== '/' && pathname.startsWith(`${target}/`));

            // aria-current is the attribute the stylesheet keys off too, so the visible
            // highlight and the announced state cannot disagree.
            if (isActive) link.setAttribute('aria-current', 'page');
            else link.removeAttribute('aria-current');
        }
    }

    function navigate(pathname, { replace = false } = {}) {
        if (pathname === window.location.pathname) return;

        if (replace) window.history.replaceState({}, '', pathname);
        else window.history.pushState({}, '', pathname);

        return render(pathname);
    }

    // Delegated once on the document rather than bound per link, so links rendered
    // later by a view need no wiring.
    document.addEventListener('click', (event) => {
        const link = event.target.closest('a[data-route], a[href^="/"]');
        if (!link) return;

        // Leave anything the user meant to open elsewhere alone: new tab, download,
        // external target, or a modified click.
        if (link.target === '_blank' || link.hasAttribute('download')) return;
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0) return;

        const url = new URL(link.href, window.location.origin);
        if (url.origin !== window.location.origin) return;

        // Anything the router does not own -- /api paths, the Scalar reference -- must
        // stay a real navigation.
        if (!routeFor(url.pathname) && url.pathname !== '/') return;

        event.preventDefault();
        navigate(url.pathname);
    });

    window.addEventListener('popstate', () => render(window.location.pathname));

    return {
        navigate,
        start: () => render(window.location.pathname, { announceChange: false }),
        refresh: () => render(window.location.pathname, { announceChange: false }),
        get current() {
            return current;
        },
    };
}
