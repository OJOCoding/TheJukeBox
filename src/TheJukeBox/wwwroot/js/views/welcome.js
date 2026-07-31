// The signed-out and first-run view.
//
// Evolved from the old landing page rather than discarded: the panel, heading, tagline
// and status indicator are the same design, because that design was the best thing about
// the original app.
//
// Two things did change. The "API CONSOLE" button is gone -- it linked to /swagger, which
// only exists in Development, so shipping it guaranteed a 404 for every real user, and it
// ejected them into a completely different visual language. And the status dot is no
// longer hardcoded green: it claimed "SYSTEM ONLINE" unconditionally, which it would have
// gone on claiming while the server was unreachable.

import { announce, el } from '../core/ui.js';
import { api } from '../core/api.js';

export function renderWelcome(outlet, { store, onSync }) {
    const { session } = store.state;
    const authenticated = session?.authenticated === true;
    const driveConnected = session?.driveConnected === true;

    const statusText = el('span', { text: 'CHECKING…' });
    const statusDot = el('div', { class: 'status-dot', 'data-state': 'offline' });

    const panel = el(
        'div',
        { class: 'panel' },
        el('div', { class: 'status' }, statusDot, statusText),
        el('h1', { text: 'JUKEBOX SYSTEM' }),
        el('p', { class: 'tagline', text: 'GOOGLE DRIVE AUDIO PLAYBACK INTERFACE' }),
        el('div', { class: 'buttons' }, ...actions()),
    );

    outlet.append(el('div', { class: 'welcome' }, panel));

    // The dot now reports something it actually measured.
    const controller = new AbortController();

    fetch('/api/health', { signal: controller.signal })
        .then((response) => {
            const ok = response.ok;
            statusDot.dataset.state = ok ? 'online' : 'offline';
            statusText.textContent = ok ? 'SYSTEM ONLINE' : 'SYSTEM DEGRADED';
        })
        .catch(() => {
            statusDot.dataset.state = 'offline';
            statusText.textContent = 'SYSTEM OFFLINE';
        });

    return () => controller.abort();

    function actions() {
        // Checked before anything else. Offering a sign-in button that cannot work sends the
        // user to Google's "Error 401: invalid_client" page, which does not say which end is
        // at fault and offers no way back. The app knows, so the app should say.
        if (session?.configured === false) {
            return [
                el('p', {
                    class: 'tagline',
                    text: 'NOT CONFIGURED: THIS INSTANCE HAS NO VALID GOOGLE OAUTH CLIENT.',
                }),
                el('p', {
                    class: 'view-header__count',
                    text: 'Set Authentication:Google:ClientId and ClientSecret from a Web '
                        + 'application OAuth client in Google Cloud Console, then restart. '
                        + 'Signing in cannot work until then.',
                }),
                el('a', {
                    class: 'btn',
                    href: 'https://console.cloud.google.com/apis/credentials',
                    target: '_blank',
                    rel: 'noreferrer noopener',
                    text: 'OPEN CLOUD CONSOLE',
                }),
            ];
        }

        if (!authenticated) {
            // A real link, not a fetch. OAuth needs a top-level navigation, and Google
            // refuses to render its consent screen inside an iframe.
            return [
                el('a', {
                    class: 'btn btn-primary',
                    href: '/api/auth/login?returnUrl=/library',
                    text: 'SIGN IN WITH GOOGLE',
                }),
            ];
        }

        if (!driveConnected) {
            return [
                el('p', {
                    class: 'tagline',
                    text: 'READ-ONLY ACCESS TO YOUR DRIVE IS REQUIRED TO LIST YOUR MUSIC.',
                }),
                el('a', {
                    class: 'btn btn-primary',
                    href: '/api/auth/login?returnUrl=/library',
                    text: 'GRANT DRIVE ACCESS',
                }),
            ];
        }

        // Signed in with a grant but no library yet: offer the first sync rather than
        // leaving an empty list with no explanation.
        return [
            el('p', { class: 'tagline', text: 'NO TRACKS YET. SCAN YOUR DRIVE TO BUILD THE LIBRARY.' }),
            el('button', {
                class: 'btn btn-primary',
                type: 'button',
                text: 'SCAN MY DRIVE',
                onclick: () => {
                    announce('Scanning your Drive.');
                    onSync();
                },
            }),
            el('a', { class: 'btn', href: '/library', 'data-route': '/library', text: 'GO TO LIBRARY' }),
        ];
    }
}

/** Progress for a first sync, polled while the view is visible. */
export function renderSyncProgress(outlet, { onDone }) {
    const heading = el('h2', { text: 'INITIAL SYNC' });
    const phase = el('p', { class: 'view-header__count', text: 'STARTING…' });
    const fill = el('div', { class: 'progress__fill' });

    const bar = el('div', {
        class: 'progress',
        role: 'progressbar',
        'aria-valuemin': '0',
        'aria-valuenow': '0',
        'aria-valuetext': 'Starting',
    }, fill);

    const listen = el('a', { class: 'btn btn-primary', href: '/library', 'data-route': '/library', text: 'START LISTENING' });
    listen.hidden = true;

    outlet.append(el('div', { class: 'welcome' }, el('div', { class: 'panel' }, heading, phase, bar, listen)));

    let stopped = false;
    let announcedAt = -1;

    async function poll() {
        if (stopped) return;

        try {
            const status = await api.syncStatus();
            const { itemsProcessed = 0, itemsTotal = 0, trackCount = 0 } = status;
            const percent = itemsTotal > 0 ? Math.round((itemsProcessed / itemsTotal) * 100) : 0;

            fill.style.inlineSize = `${percent}%`;
            bar.setAttribute('aria-valuenow', String(percent));
            bar.setAttribute('aria-valuetext', `${percent} percent, ${itemsProcessed} of ${itemsTotal} files`);
            phase.textContent = itemsTotal > 0
                ? `${status.phase ?? 'WORKING'} — ${itemsProcessed} / ${itemsTotal}`
                : (status.phase ?? 'WORKING').toUpperCase();

            // Announced at quarter marks only. Narrating every file would be hostile,
            // which is also why the file list itself is not a live region.
            const milestone = Math.floor(percent / 25) * 25;
            if (milestone !== announcedAt && percent > 0) {
                announcedAt = milestone;
                announce(`Sync ${milestone} percent complete.`);
            }

            // The user does not wait for 100%: as soon as anything is playable, let them
            // go. Blocking a large first scan behind a spinner loses people.
            if (trackCount > 0) {
                listen.hidden = false;
                listen.textContent = `START LISTENING (${trackCount} TRACKS READY)`;
            }

            if (status.status === 'Succeeded' || status.status === 'Failed') {
                if (status.status === 'Failed') {
                    phase.textContent = (status.lastError ?? 'SYNC FAILED').toUpperCase();
                } else {
                    onDone(trackCount);
                }
                return;
            }
        } catch {
            // A failed poll is not a failed sync. Keep trying.
        }

        setTimeout(poll, 1000);
    }

    poll();

    return () => { stopped = true; };
}
