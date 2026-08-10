// What is playing, at a size you can see from across the room.
//
// The transport at the bottom of the shell is deliberately small -- it has to coexist with every
// other view. This is the view that does not have to compromise: one large cover, the title, the
// artist and the album, and the play bar sitting below it.
//
// It reads entirely from the store, so it needs no requests of its own. That also means it stays
// correct while a track changes underneath it, which for the one screen a user is likely to leave
// open is the property that matters.

import { alertUser, announce, el, mmss } from '../core/ui.js';
import { api } from '../core/api.js';

/** How many recent listens to show. Enough to recognise the session, short enough to scan. */
const HISTORY_LIMIT = 12;

/**
 * "3 minutes ago", "yesterday".
 *
 * Relative rather than a clock time, because the question this list answers is "what was I
 * just listening to", and 14:32 does not answer it without arithmetic.
 */
function ago(iso) {
    const then = Date.parse(iso);

    if (!Number.isFinite(then)) return '';

    const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));

    if (seconds < 90) return 'JUST NOW';

    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${minutes} MIN AGO`;

    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours} H AGO`;

    const days = Math.round(hours / 24);
    return days === 1 ? 'YESTERDAY' : `${days} DAYS AGO`;
}

export function renderNowPlaying(outlet, { store, player }) {
    const controller = new AbortController();
    const art = el('div', { class: 'now__art', 'aria-hidden': 'true' });
    const title = el('h1', { class: 'now__title', text: '' });
    const artist = el('p', { class: 'now__artist', text: '' });
    const album = el('p', { class: 'now__album', text: '' });
    const facts = el('p', { class: 'now__facts', text: '' });
    const actions = el('div', { class: 'now__actions' });

    // A live region, so a screen reader user who is sitting on this view is told when the track
    // changes. Polite: a track change is not an interruption.
    const panel = el(
        'section',
        { class: 'now', 'aria-live': 'polite', 'aria-atomic': 'false', 'aria-label': 'Now playing' },
        art,
        el('div', { class: 'now__meta' }, title, artist, album, facts, actions),
    );

    // The listening log, which has been collected since B2 and shown nowhere. Its own region so
    // a screen reader can skip it, and outside the now-playing live region so refreshing it does
    // not announce twelve track names.
    const historyBody = el('div', { class: 'history__body' });

    const history = el(
        'section',
        { class: 'history', 'aria-label': 'Recently played' },
        el('h2', { class: 'history__heading', text: 'RECENTLY PLAYED' }),
        historyBody,
    );

    outlet.append(panel, history);

    /** The art currently shown, so an unchanged track does not restart the image load. */
    let shownArt = null;

    function renderArt(track) {
        const sha = track?.artSha256 ?? null;

        if (sha === shownArt) return;

        shownArt = sha;

        if (!sha) {
            art.replaceChildren(el('span', { class: 'now__glyph', text: '♪' }));
            return;
        }

        const image = new Image();

        // 640, the large variant. This is the one place in the app where cover art is the point
        // rather than a thumbnail beside a row.
        image.src = `/api/art/${encodeURIComponent(sha)}/640`;
        image.alt = '';

        image.addEventListener('error', () => art.replaceChildren(el('span', { class: 'now__glyph', text: '♪' })));
        image.addEventListener('load', () => art.replaceChildren(image));
    }

    function render(state) {
        const track = state.currentTrack;

        if (!track) {
            title.textContent = 'NOTHING PLAYING';
            artist.textContent = '';
            album.textContent = '';
            facts.textContent = state.queueLength > 0
                ? `${state.queueLength} track${state.queueLength === 1 ? '' : 's'} queued — press play`
                : 'Pick something from your library, an album, or a playlist.';

            renderArt(null);

            actions.replaceChildren(el('a', {
                class: 'btn',
                href: '/library',
                'data-route': '/library',
                text: 'BROWSE THE LIBRARY',
            }));

            return;
        }

        title.textContent = track.title;
        artist.textContent = track.artist ?? '';
        album.textContent = track.album ?? '';

        // Position out of duration, in words this view has room for. The transport shows the same
        // thing as a bar; this is the readable version.
        const known = Number.isFinite(state.duration) && state.duration > 0;

        facts.textContent = [
            state.playing ? 'PLAYING' : 'PAUSED',
            known ? `${mmss(state.position)} / ${mmss(state.duration)}` : '',
            state.mode.shuffle ? 'SHUFFLE' : '',
            state.mode.repeat === 'all' ? 'REPEAT ALL' : state.mode.repeat === 'one' ? 'REPEAT ONE' : '',
        ].filter(Boolean).join(' · ');

        renderArt(track);

        actions.replaceChildren(
            el('a', { class: 'btn btn--sm', href: '/queue', 'data-route': '/queue', text: 'VIEW QUEUE' }),
        );
    }

    /**
     * Loads the listening log.
     *
     * Collapsed to one row per track. The log is a log -- with repeat-one on it will hold the
     * same title twenty times -- and "recently played" as a *list of tracks* is what someone
     * looking at this actually wants.
     */
    async function loadHistory() {
        try {
            const entries = await api.history(HISTORY_LIMIT * 3);

            if (controller.signal.aborted) return;

            const seen = new Set();
            const unique = [];

            for (const entry of entries) {
                if (seen.has(entry.trackId)) continue;

                seen.add(entry.trackId);
                unique.push(entry);

                if (unique.length === HISTORY_LIMIT) break;
            }

            if (unique.length === 0) {
                historyBody.replaceChildren(el('p', {
                    class: 'view-header__count',
                    text: 'NOTHING YET. PLAY SOMETHING FOR TWENTY SECONDS AND IT WILL APPEAR HERE.',
                }));
                return;
            }

            const list = el('ol', { class: 'queue', 'aria-label': 'Recently played tracks' });

            for (const entry of unique) list.append(historyRow(entry));

            historyBody.replaceChildren(list);
        } catch {
            if (controller.signal.aborted) return;

            // A failed history load is not worth a toast. The rest of the view is the point, and
            // this half is a convenience -- saying so quietly is proportionate.
            historyBody.replaceChildren(el('p', {
                class: 'view-header__count',
                text: 'COULD NOT LOAD YOUR LISTENING HISTORY.',
            }));
        }
    }

    function historyRow(entry) {
        const label = `${entry.title}${entry.artist ? ` by ${entry.artist}` : ''}`;

        const playButton = el('button', {
            class: 'queue__play',
            type: 'button',
            disabled: !entry.isAvailable,
            'aria-label': entry.isAvailable ? `Play ${label} again` : `${label} is no longer in your library`,
            onclick: () => replay(entry),
        },
            el('span', { class: 'queue__title', text: entry.title }),
            el('span', { class: 'queue__artist', text: entry.artist ?? '' }));

        const when = el('span', { class: 'queue__duration', text: ago(entry.playedAtUtc) });

        const row = el('li', { class: 'queue__row' }, playButton, when);

        if (!entry.isAvailable) row.dataset.unavailable = '1';

        return row;
    }

    /** Plays one track from the log, on its own, without disturbing the saved queue's order. */
    function replay(entry) {
        // The catalogue may never have heard of this track: the history endpoint carries its own
        // metadata precisely so this view needs no library page to have loaded first.
        player.mergeCatalogue([{
            id: entry.trackId,
            title: entry.title,
            artist: entry.artist,
            album: entry.album,
            durationSeconds: null,
            artSha256: entry.artSha256,
        }]);

        if (player.playAll([entry.trackId])) announce(`Playing ${entry.title}.`);
        else alertUser(`${entry.title} could not be played.`);
    }

    render(store.state);
    void loadHistory();

    /** The track the log was last loaded for, so it refreshes when one finishes rather than on
     *  every position tick -- the store publishes several of those a second. */
    let lastTrackId = store.state.currentTrackId;

    // Subscribed rather than polled. The store already ticks on every position update, which is
    // exactly the cadence this view wants for its clock.
    const unsubscribe = store.subscribe((state) => {
        render(state);

        // A listen is written to the log when it *ends*, which is exactly when the current track
        // changes. That makes this the one moment the log is worth re-reading.
        if (state.currentTrackId !== lastTrackId) {
            lastTrackId = state.currentTrackId;
            void loadHistory();
        }
    });

    return () => {
        controller.abort();
        unsubscribe();
    };
}
