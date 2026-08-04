// Browsing by album and by artist.
//
// Four views over the same aggregates: a grid of albums, one album's tracks, a list of artists,
// and one artist's albums and tracks. The server does the grouping (see BrowseEndpoints), so
// these are presentation only.
//
// Album art is the point of the albums grid, which is why it is a grid rather than another list:
// a wall of covers is how people recognise their own music, and the art has been extracted and
// content-addressed since B1 without anything ever showing it at size.

import { alertUser, announce, clear, el, isoDuration, mmss } from '../core/ui.js';
import { ApiError, api } from '../core/api.js';

/** "1 H 12 M", or "4 M". For a whole album, where seconds are noise. */
function runtime(seconds) {
    if (!Number.isFinite(seconds) || seconds <= 0) return '';

    const hours = Math.floor(seconds / 3600);
    const minutes = Math.round((seconds % 3600) / 60);

    return hours > 0 ? `${hours} H ${minutes} M` : `${minutes} M`;
}

/** What to call a group whose tag was missing. */
const UNKNOWN_ALBUM = 'NO ALBUM';
const UNKNOWN_ARTIST = 'NO ARTIST';

/** "1 track" / "12 tracks". Accessible names are read aloud, so the grammar shows. */
function tracks(count) {
    return `${count} track${count === 1 ? '' : 's'}`;
}

/**
 * A cover image, or a placeholder glyph.
 *
 * @param {string} size the art endpoint's size variant: 96 or 640.
 */
function cover(artSha256, size) {
    const frame = el('div', { class: 'cover', 'aria-hidden': 'true' });

    if (!artSha256) {
        frame.append(el('span', { class: 'cover__glyph', text: '♪' }));
        return frame;
    }

    const image = new Image();

    // Decorative: the album title is beside it as text, so alt is empty rather than repeating it.
    image.alt = '';
    image.loading = 'lazy';
    image.src = `/api/art/${encodeURIComponent(artSha256)}/${size}`;

    // A missing or corrupt image must not leave an empty frame in the grid.
    image.addEventListener('error', () => frame.replaceChildren(el('span', { class: 'cover__glyph', text: '♪' })));

    frame.append(image);

    return frame;
}

/** The albums grid. */
export function renderAlbums(outlet, { player }) {
    const controller = new AbortController();

    const count = el('p', { class: 'view-header__count', text: 'LOADING…' });
    const body = el('div', {});

    const filter = el('input', {
        type: 'search',
        id: 'albumFilter',
        placeholder: 'FILTER ALBUMS',
        'aria-label': 'Filter albums by title or artist',
        autocomplete: 'off',
    });

    outlet.append(
        el('div', { class: 'view-header' },
            el('h2', { text: 'ALBUMS' }),
            count,
            el('form', { class: 'field', role: 'search', onsubmit: (e) => e.preventDefault() },
                el('span', { class: 'field__prompt', 'aria-hidden': 'true', text: '>' }),
                filter)),
        body,
    );

    /** @type {Array<object>} */
    let albums = [];

    filter.addEventListener('input', () => paint());

    async function load() {
        try {
            const page = await api.albums({ signal: controller.signal });

            albums = page.items;
            paint(page.truncated);
        } catch (error) {
            if (error.name === 'AbortError') return;

            count.textContent = 'COULD NOT LOAD ALBUMS';
            body.replaceChildren(errorNotice(error, () => load()));
        }
    }

    function paint(truncated = false) {
        // Filtered in the browser rather than by a request per keystroke. The whole album list is
        // already here and is at most a thousand rows, so a round trip would be slower than the
        // filter it is replacing.
        const needle = filter.value.trim().toLowerCase();

        const shown = needle.length === 0
            ? albums
            : albums.filter((a) =>
                (a.album ?? '').toLowerCase().includes(needle)
                || (a.albumArtist ?? '').toLowerCase().includes(needle));

        count.textContent = [
            `${shown.length}${needle ? ' MATCHING' : ''} ALBUM${shown.length === 1 ? '' : 'S'}`,
            truncated ? 'LIST TRUNCATED' : '',
        ].filter(Boolean).join(' — ');

        if (shown.length === 0) {
            body.replaceChildren(el('div', { class: 'notice' },
                el('p', { text: needle ? 'NO ALBUMS MATCH.' : 'NO ALBUMS YET.' })));
            return;
        }

        const grid = el('ul', { class: 'grid', 'aria-label': 'Albums' });

        for (const album of shown) {
            grid.append(albumCard(album));
        }

        body.replaceChildren(grid);
    }

    function albumCard(album) {
        const title = album.album || UNKNOWN_ALBUM;
        const artist = album.albumArtist || UNKNOWN_ARTIST;

        const link = el('a', {
            class: 'grid__link',
            href: `/albums/${encodeURIComponent(album.key)}`,

            // The visible text is the title; the accessible name adds the artist and size, because
            // "Greatest Hits" on its own is not a distinguishable link name.
            'aria-label': `${title} by ${artist}, ${tracks(album.trackCount)}`,
        }, cover(album.artSha256, 96), el('span', { class: 'grid__title', text: title }));

        return el('li', { class: 'grid__item' },
            link,
            el('span', { class: 'grid__sub', text: artist }),
            el('span', { class: 'grid__facts', text: [
                `${album.trackCount} TRACK${album.trackCount === 1 ? '' : 'S'}`,
                album.year ? String(album.year) : '',
                runtime(album.durationSeconds),
            ].filter(Boolean).join(' · ') }),
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'PLAY',
                'aria-label': `Play ${title} by ${artist}`,
                onclick: () => playGroup(player, () => api.album(album.key), title),
            }));
    }

    load();

    return () => controller.abort();
}

/** One album's tracks, in disc and track order. */
export function renderAlbum(outlet, { player, store }, params) {
    return renderGroupDetail(outlet, { player, store }, {
        load: (signal) => api.album(params.key, { signal }),
        backHref: '/albums',
        backText: 'ALL ALBUMS',
        missing: 'THIS ALBUM IS NO LONGER IN YOUR LIBRARY.',
        heading: (detail) => detail.album || UNKNOWN_ALBUM,
        subheading: (detail) => detail.albumArtist || UNKNOWN_ARTIST,

        // Linked back to the artist, which is the navigation people actually want from an album.
        subheadingHref: (detail) => `/artists/${encodeURIComponent(artistKeyOf(detail))}`,
        artOf: (detail) => detail.artSha256,
        tracksOf: (detail) => detail.tracks,

        // Track numbers, because on an album they mean something. The library view numbers rows
        // by position instead, which is right there and wrong here.
        numberOf: (track) => track.trackNumber,
        secondaryOf: (track) => track.artist,
    });
}

/** The artists list. */
export function renderArtists(outlet, { player }) {
    const controller = new AbortController();

    const count = el('p', { class: 'view-header__count', text: 'LOADING…' });
    const body = el('div', {});

    const filter = el('input', {
        type: 'search',
        id: 'artistFilter',
        placeholder: 'FILTER ARTISTS',
        'aria-label': 'Filter artists by name',
        autocomplete: 'off',
    });

    outlet.append(
        el('div', { class: 'view-header' },
            el('h2', { text: 'ARTISTS' }),
            count,
            el('form', { class: 'field', role: 'search', onsubmit: (e) => e.preventDefault() },
                el('span', { class: 'field__prompt', 'aria-hidden': 'true', text: '>' }),
                filter)),
        body,
    );

    let artists = [];

    filter.addEventListener('input', () => paint());

    async function load() {
        try {
            const page = await api.artists({ signal: controller.signal });

            artists = page.items;
            paint(page.truncated);
        } catch (error) {
            if (error.name === 'AbortError') return;

            count.textContent = 'COULD NOT LOAD ARTISTS';
            body.replaceChildren(errorNotice(error, () => load()));
        }
    }

    function paint(truncated = false) {
        const needle = filter.value.trim().toLowerCase();

        const shown = needle.length === 0
            ? artists
            : artists.filter((a) => (a.artist ?? '').toLowerCase().includes(needle));

        count.textContent = [
            `${shown.length}${needle ? ' MATCHING' : ''} ARTIST${shown.length === 1 ? '' : 'S'}`,
            truncated ? 'LIST TRUNCATED' : '',
        ].filter(Boolean).join(' — ');

        if (shown.length === 0) {
            body.replaceChildren(el('div', { class: 'notice' },
                el('p', { text: needle ? 'NO ARTISTS MATCH.' : 'NO ARTISTS YET.' })));
            return;
        }

        const list = el('ul', { class: 'cards', 'aria-label': 'Artists' });

        for (const artist of shown) {
            const name = artist.artist || UNKNOWN_ARTIST;

            list.append(el('li', { class: 'cards__item' },
                el('div', { class: 'cards__body' },
                    el('a', {
                        class: 'cards__link',
                        href: `/artists/${encodeURIComponent(artist.key)}`,
                        text: name,
                    }),
                    el('span', { class: 'cards__facts', text: [
                        `${artist.albumCount} ALBUM${artist.albumCount === 1 ? '' : 'S'}`,
                        `${artist.trackCount} TRACK${artist.trackCount === 1 ? '' : 'S'}`,
                        runtime(artist.durationSeconds),
                    ].filter(Boolean).join(' — ') })),
                el('div', { class: 'cards__actions' },
                    el('button', {
                        class: 'btn btn--sm',
                        type: 'button',
                        text: 'PLAY',
                        'aria-label': `Play everything by ${name}`,
                        onclick: () => playGroup(player, () => api.artist(artist.key), name),
                    }))));
        }

        body.replaceChildren(list);
    }

    load();

    return () => controller.abort();
}

/** One artist: their albums, then every track. */
export function renderArtist(outlet, { player, store }, params) {
    return renderGroupDetail(outlet, { player, store }, {
        load: (signal) => api.artist(params.key, { signal }),
        backHref: '/artists',
        backText: 'ALL ARTISTS',
        missing: 'THIS ARTIST IS NO LONGER IN YOUR LIBRARY.',
        heading: (detail) => detail.artist || UNKNOWN_ARTIST,
        subheading: (detail) => [
            `${detail.albums.length} ALBUM${detail.albums.length === 1 ? '' : 'S'}`,
            `${detail.trackCount} TRACK${detail.trackCount === 1 ? '' : 'S'}`,
        ].join(' — '),
        artOf: (detail) => detail.artSha256,
        tracksOf: (detail) => detail.tracks,

        // Album rather than track number: an artist's track list spans records, so a column of
        // "1, 2, 3, 1, 2" would be noise. The album name is what locates a track here.
        numberOf: () => null,
        secondaryOf: (track) => track.album,

        // The albums strip, above the track list.
        extra: (detail) => {
            if (detail.albums.length <= 1) return null;

            const strip = el('ul', { class: 'grid grid--compact', 'aria-label': `Albums by ${detail.artist || UNKNOWN_ARTIST}` });

            for (const album of detail.albums) {
                const title = album.album || UNKNOWN_ALBUM;

                strip.append(el('li', { class: 'grid__item' },
                    el('a', {
                        class: 'grid__link',
                        href: `/albums/${encodeURIComponent(album.key)}`,
                        'aria-label': `${title}, ${tracks(album.trackCount)}`,
                    }, cover(album.artSha256, 96), el('span', { class: 'grid__title', text: title })),
                    el('span', { class: 'grid__facts', text: `${album.trackCount} TRACK${album.trackCount === 1 ? '' : 'S'}` })));
            }

            return strip;
        },
    });
}

/**
 * The shared detail view for an album or an artist.
 *
 * Both screens are the same shape -- a header with art, an optional strip, and a track list where
 * clicking a track plays the group from that point. Writing them twice would mean fixing the
 * keyboard behaviour and the play-from-here arithmetic twice.
 */
function renderGroupDetail(outlet, { player }, options) {
    const controller = new AbortController();

    const heading = el('h2', { text: 'LOADING…' });
    const sub = el('p', { class: 'view-header__count', text: '' });
    const actions = el('div', { class: 'view-header__actions' });
    const body = el('div', {});

    outlet.append(el('div', { class: 'view-header' }, heading, sub, actions), body);

    async function load() {
        try {
            const detail = await options.load(controller.signal);

            if (controller.signal.aborted) return;

            paint(detail);
        } catch (error) {
            if (error.name === 'AbortError') return;

            if (error instanceof ApiError && error.status === 404) {
                heading.textContent = 'NOT FOUND';
                sub.textContent = '';
                clear(actions);
                body.replaceChildren(el('div', { class: 'notice' },
                    el('p', { text: options.missing }),
                    el('a', {
                        class: 'btn',
                        href: options.backHref,
                        'data-route': options.backHref,
                        text: options.backText,
                    })));
                return;
            }

            heading.textContent = 'ERROR';
            body.replaceChildren(errorNotice(error, () => load()));
        }
    }

    function paint(detail) {
        const title = options.heading(detail);
        const tracks = options.tracksOf(detail);

        heading.textContent = title;
        document.title = `${title} — JUKEBOX`;

        const subText = options.subheading(detail);
        const subHref = options.subheadingHref?.(detail);

        // A link when there is somewhere to go, plain text otherwise. An anchor with no
        // destination is a worse affordance than no anchor.
        sub.replaceChildren(subHref
            ? el('a', { href: subHref, text: subText })
            : document.createTextNode(subText));

        clear(actions);

        actions.append(
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'PLAY',
                'aria-label': `Play ${title}`,
                onclick: () => play(tracks, 0, title),
            }),
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'ADD TO QUEUE',
                'aria-label': `Add ${title} to the queue`,
                onclick: () => {
                    const added = player.enqueue(tracks.map((t) => t.id));

                    if (added === 0) alertUser('Nothing here could be added to the queue.');
                },
            }),
            el('a', {
                class: 'btn btn--sm',
                href: options.backHref,
                'data-route': options.backHref,
                text: options.backText,
            }),
        );

        const list = el('ol', { class: 'queue', 'aria-label': `Tracks in ${title}` });

        for (const [index, track] of tracks.entries()) {
            list.append(row(track, index, tracks, title));
        }

        const extra = options.extra?.(detail);

        body.replaceChildren(
            el('div', { class: 'group-header' },
                cover(options.artOf(detail), 640),
                el('div', { class: 'group-header__facts' },
                    el('span', { text: `${tracks.length} TRACK${tracks.length === 1 ? '' : 'S'}` }),
                    el('span', { text: runtime(detail.durationSeconds) }))),
            ...(extra ? [extra] : []),
            list,
        );
    }

    function row(track, index, tracks, title) {
        const number = options.numberOf(track);
        const secondary = options.secondaryOf(track);

        // Named playButton, not play: a local `play` would shadow the play() function below, and
        // the click handler would then call the DOM element instead. That is not a hypothetical --
        // it is what this line did until a browser check caught "play is not a function".
        const playButton = el('button', {
            class: 'queue__play',
            type: 'button',
            'aria-label': `Play ${track.title}${secondary ? `, ${secondary}` : ''}`,
            onclick: () => play(tracks, index, title),
        },
            el('span', { class: 'queue__title', text: number ? `${number}. ${track.title}` : track.title }),
            el('span', { class: 'queue__artist', text: secondary ?? '' }));

        const duration = el('time', { class: 'queue__duration', text: mmss(track.durationSeconds) });
        const iso = isoDuration(track.durationSeconds);
        if (iso) duration.setAttribute('datetime', iso);

        return el('li', { class: 'queue__row' }, playButton, duration);
    }

    function play(tracks, startAt, title) {
        // The catalogue is primed from what is on screen, or the player cannot name or stream
        // tracks the library view has never loaded -- which for an album deep link is all of them.
        player.mergeCatalogue(tracks.map((t) => ({
            id: t.id,
            title: t.title,
            artist: t.artist,
            album: t.album,
            durationSeconds: t.durationSeconds,
            artSha256: t.artSha256,
        })));

        if (player.playAll(tracks.map((t) => t.id), { startAt })) announce(`Playing ${title}.`);
    }

    load();

    return () => controller.abort();
}

/** Loads a group's detail and plays it from the top. */
async function playGroup(player, load, name) {
    try {
        const detail = await load();
        const tracks = detail.tracks ?? [];

        if (tracks.length === 0) {
            alertUser(`${name} has nothing that can be played.`);
            return;
        }

        player.mergeCatalogue(tracks.map((t) => ({
            id: t.id,
            title: t.title,
            artist: t.artist,
            album: t.album,
            durationSeconds: t.durationSeconds,
            artSha256: t.artSha256,
        })));

        if (player.playAll(tracks.map((t) => t.id))) announce(`Playing ${name}.`);
    } catch (error) {
        alertUser(error.message);
    }
}

/**
 * The album's artist key, for the link from an album back to its artist.
 *
 * Derived on the client because the album response already carries the artist name and adding a
 * second key to the payload for one link is not worth the API surface. The encoding must match
 * the server's GroupKey exactly, which is what the shared test asserts.
 */
function artistKeyOf(detail) {
    return encodeArtistKey(detail.albumArtist ?? '');
}

/**
 * Base64url of an artist key, matching the server's `GroupKey.ForArtist`.
 *
 * The 'a' marker is what keeps an artist key distinguishable from an album key, and what stops
 * the key for an absent name encoding to an empty string -- which would be an unreachable URL.
 */
export function encodeArtistKey(artist) {
    const bytes = new TextEncoder().encode(`a${artist ?? ''}`);

    // btoa works on a byte string, so the UTF-8 bytes are mapped to code points first. Passing the
    // original string would throw on any non-Latin-1 character, which artist names routinely have.
    let binary = '';
    for (const byte of bytes) binary += String.fromCharCode(byte);

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function errorNotice(error, retry) {
    return el('div', { class: 'notice notice--error' },
        el('p', { text: error.message }),
        el('button', { class: 'btn', type: 'button', text: 'RETRY', onclick: retry }));
}
