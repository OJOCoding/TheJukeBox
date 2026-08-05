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

        // A description, because "GOOGLE DRIVE AUDIO PLAYBACK INTERFACE" tells you the category
        // and nothing about the idea. Deliberately plain-spoken next to the shouty monospace.
        el('div', { class: 'blurb' },
            el('p', {
                text: 'You already own a pile of music. It is sitting in Google Drive in folders '
                    + 'with names like "new stuff (2)", and the only way to play it is to '
                    + 'download it again.',
            }),
            el('p', {
                text: 'This turns that pile into a record collection. It reads the tags inside '
                    + 'your files — artist, album, the cover art nobody knew was in there — and '
                    + 'gives you a library you can search, albums you can browse, playlists, a '
                    + 'queue, and a resume point that survives closing the tab.',
            }),
            el('p', {
                text: 'Nothing is copied anywhere. Your files stay in your Drive, read-only, and '
                    + 'the audio is streamed straight through. Self-hosted, single binary, one '
                    + 'SQLite file.',
            })),

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

                // The value actually read, redacted. Without it, someone who has just filled in
                // credentials has no way to tell that this process is reading a different source
                // -- and since .env applies to docker compose only, that is the common case.
                session?.clientIdHint
                    ? el('p', {
                        class: 'view-header__count',
                        text: `The client id this instance read is: ${session.clientIdHint}`,
                    })
                    : null,

                el('p', {
                    class: 'view-header__count',
                    text: 'If that is not the id you configured, it was set somewhere this '
                        + 'process does not read. .env is used by docker compose only and has no '
                        + 'effect on dotnet run; running locally, credentials come from '
                        + 'user-secrets.',
                }),
                el('p', {
                    class: 'view-header__count',
                    text: 'Docker: copy .env.example to .env, fill in JUKEBOX_CLIENT_ID and '
                        + 'JUKEBOX_CLIENT_SECRET, then docker compose up -d. Running locally: '
                        + 'cd src/TheJukeBox and run dotnet user-secrets set '
                        + '"Authentication:Google:ClientId" "<your id>", the same for '
                        + 'ClientSecret, then restart.',
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

        // Signed in, but not admitted. Checked before the Drive branch: asking someone to
        // grant Drive access they cannot use yet would be a pointless consent screen, and it
        // would burn one of the Cloud project's permanent 100 unverified-app user slots.
        if (session?.status === 'Disabled') {
            return [
                el('p', { class: 'tagline', text: 'THIS ACCOUNT HAS BEEN DISABLED.' }),
                el('p', {
                    class: 'view-header__count',
                    text: 'An administrator of this instance has turned off access for this '
                        + 'account. Contact them if you think this is a mistake.',
                }),
                signOutLink(),
            ];
        }

        if (session?.status !== 'Active') {
            const singleUser = session?.registration === 'SingleUser';

            return [
                el('p', {
                    class: 'tagline',
                    text: singleUser
                        ? 'THIS INSTANCE IS FOR A SINGLE USER.'
                        : 'YOUR ACCOUNT IS WAITING FOR APPROVAL.',
                }),
                el('p', {
                    class: 'view-header__count',

                    // Told plainly under SingleUser. Nobody is ever going to approve this
                    // account, and "please wait" for something that will never happen wastes
                    // the person's time and the operator's.
                    text: singleUser
                        ? 'Only its owner can have an account here, so this one will not be '
                        + 'approved. Nothing of yours has been read or stored beyond your '
                        + 'name and email address.'
                        : 'Signing in worked. An administrator of this instance has to approve '
                        + 'this account before your library can be listed. Check back later, '
                        + 'or ask them directly.',
                }),
                signOutLink(),
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

    /**
     * A way out for an account that is not admitted.
     *
     * The top bar, which holds the usual SIGN OUT button, is hidden until an account is
     * approved -- so without this a pending or disabled user has no way to leave the session,
     * including no way to try a different Google account.
     */
    function signOutLink() {
        return el('button', {
            class: 'btn',
            type: 'button',
            text: 'SIGN OUT',
            onclick: async (event) => {
                event.currentTarget.disabled = true;

                try {
                    await api.signOut();
                } finally {
                    // A full navigation rather than a router push, for the same reason as the
                    // top bar's button: no state from the previous session should survive.
                    window.location.assign('/');
                }
            },
        });
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
