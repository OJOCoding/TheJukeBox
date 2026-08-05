// What is playing, at a size you can see from across the room.
//
// The transport at the bottom of the shell is deliberately small -- it has to coexist with every
// other view. This is the view that does not have to compromise: one large cover, the title, the
// artist and the album, and the play bar sitting below it.
//
// It reads entirely from the store, so it needs no requests of its own. That also means it stays
// correct while a track changes underneath it, which for the one screen a user is likely to leave
// open is the property that matters.

import { el, mmss } from '../core/ui.js';

export function renderNowPlaying(outlet, { store }) {
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

    outlet.append(panel);

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

    render(store.state);

    // Subscribed rather than polled. The store already ticks on every position update, which is
    // exactly the cadence this view wants for its clock.
    const unsubscribe = store.subscribe(render);

    return unsubscribe;
}
