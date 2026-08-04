// The playlists index, and the playlist detail view.
//
// Reordering is done with move-up/move-down buttons rather than drag-and-drop. Drag-and-drop
// alone is unusable by keyboard and awkward by touch, so it would have needed a keyboard
// equivalent anyway -- and once the buttons exist they are the whole feature. Dragging can be
// added on top later without changing the model.

import { alertUser, announce, clear, el, isoDuration, mmss } from '../core/ui.js';
import { ApiError, api } from '../core/api.js';

/** "1 h 12 m", or "4 m". For a whole playlist, where seconds are noise. */
function runtime(seconds) {
    if (!Number.isFinite(seconds) || seconds <= 0) return '';

    const hours = Math.floor(seconds / 3600);
    const minutes = Math.round((seconds % 3600) / 60);

    return hours > 0 ? `${hours} H ${minutes} M` : `${minutes} M`;
}

export function renderPlaylists(outlet, { player, store }) {
    const controller = new AbortController();

    const count = el('p', { class: 'view-header__count', text: 'LOADING…' });
    const body = el('div', {});

    const name = el('input', {
        type: 'text',
        id: 'newPlaylistName',
        placeholder: 'NEW PLAYLIST NAME',
        'aria-label': 'Name for a new playlist',
        maxLength: 200,
        autocomplete: 'off',
    });

    const create = el('form', {
        class: 'field',
        onsubmit: async (event) => {
            event.preventDefault();

            const value = name.value.trim();

            if (value.length === 0) {
                alertUser('A playlist needs a name.');
                name.focus();
                return;
            }

            try {
                const playlist = await api.createPlaylist(value);

                name.value = '';
                announce(`Playlist ${playlist.name} created.`);

                await load();
            } catch (error) {
                alertUser(error.message);
            }
        },
    },
        el('span', { class: 'field__prompt', 'aria-hidden': 'true', text: '>' }),
        name,
        el('button', { class: 'btn btn--sm', type: 'submit', text: 'CREATE' }));

    outlet.append(
        el('div', { class: 'view-header' }, el('h2', { text: 'PLAYLISTS' }), count, create),
        body,
    );

    async function load() {
        try {
            const playlists = await api.playlists();

            if (controller.signal.aborted) return;

            count.textContent = playlists.length === 0
                ? 'NONE YET'
                : `${playlists.length} PLAYLIST${playlists.length === 1 ? '' : 'S'}`;

            if (playlists.length === 0) {
                body.replaceChildren(el('div', { class: 'notice' },
                    el('p', { text: 'NO PLAYLISTS YET.' }),
                    el('p', {
                        class: 'view-header__count',
                        text: 'Name one above, then add tracks to it from the library.',
                    })));
                return;
            }

            const list = el('ul', { class: 'cards', 'aria-label': 'Playlists' });

            for (const playlist of playlists) {
                list.append(card(playlist));
            }

            body.replaceChildren(list);
        } catch (error) {
            if (controller.signal.aborted) return;

            count.textContent = 'COULD NOT LOAD PLAYLISTS';
            alertUser(error.message);
        }
    }

    function card(playlist) {
        const facts = [
            `${playlist.trackCount} TRACK${playlist.trackCount === 1 ? '' : 'S'}`,
            runtime(playlist.durationSeconds),
        ].filter(Boolean).join(' — ');

        const open = el('a', {
            class: 'cards__link',
            href: `/playlists/${encodeURIComponent(playlist.id)}`,
            text: playlist.name,
        });

        const play = el('button', {
            class: 'btn btn--sm',
            type: 'button',
            'aria-label': `Play ${playlist.name}`,
            text: 'PLAY',

            // Empty playlists cannot be played, and a button that always fails is worse than a
            // disabled one.
            disabled: playlist.trackCount === 0,
            onclick: () => playPlaylist(player, playlist.id, playlist.name),
        });

        return el(
            'li',
            { class: 'cards__item' },
            el('div', { class: 'cards__body' },
                open,
                el('span', { class: 'cards__facts', text: facts }),
                playlist.description
                    ? el('span', { class: 'cards__facts', text: playlist.description })
                    : null,
                playlist.hasUnavailableTracks
                    ? el('span', { class: 'cards__facts', text: 'SOME TRACKS ARE UNAVAILABLE' })
                    : null),
            el('div', { class: 'cards__actions' }, play),
        );
    }

    load();

    return () => controller.abort();
}

/**
 * The detail view for one playlist.
 *
 * @param {object} params the router's `:id` capture.
 */
export function renderPlaylistDetail(outlet, { player, store }, params) {
    const controller = new AbortController();
    const playlistId = params.id;

    const heading = el('h2', { text: 'PLAYLIST' });
    const count = el('p', { class: 'view-header__count', text: 'LOADING…' });
    const body = el('div', {});
    const actions = el('div', { class: 'view-header__actions' });

    outlet.append(el('div', { class: 'view-header' }, heading, count, actions), body);

    /** @type {Array<object>} the items as last loaded, in playlist order. */
    let items = [];
    let title = '';

    async function load() {
        try {
            const detail = await api.playlist(playlistId);

            if (controller.signal.aborted) return;

            items = detail.items;
            title = detail.name;

            heading.textContent = detail.name;
            document.title = `${detail.name} — JUKEBOX`;

            const playable = items.filter((item) => item.isAvailable).length;
            const seconds = items
                .filter((item) => item.isAvailable)
                .reduce((total, item) => total + (item.durationSeconds ?? 0), 0);

            count.textContent = [
                `${playable} TRACK${playable === 1 ? '' : 'S'}`,
                runtime(seconds),
                items.length !== playable ? `${items.length - playable} UNAVAILABLE` : '',
            ].filter(Boolean).join(' — ');

            renderActions();
            renderItems();
        } catch (error) {
            if (controller.signal.aborted) return;

            if (error instanceof ApiError && error.status === 404) {
                heading.textContent = 'NOT FOUND';
                count.textContent = '';
                body.replaceChildren(el('div', { class: 'notice' },
                    el('p', { text: 'THIS PLAYLIST DOES NOT EXIST.' }),
                    el('a', { class: 'btn', href: '/playlists', 'data-route': '/playlists', text: 'ALL PLAYLISTS' })));
                return;
            }

            count.textContent = 'COULD NOT LOAD THIS PLAYLIST';
            alertUser(error.message);
        }
    }

    function renderActions() {
        clear(actions);

        actions.append(
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'PLAY',
                disabled: items.every((item) => !item.isAvailable),
                onclick: () => playPlaylist(player, playlistId, title),
            }),
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'ADD TO QUEUE',
                disabled: items.every((item) => !item.isAvailable),
                onclick: async () => {
                    try {
                        const ids = await api.playlistTrackIds(playlistId);
                        const added = player.enqueue(ids);

                        if (added === 0) alertUser('Nothing in this playlist could be queued.');
                    } catch (error) {
                        alertUser(error.message);
                    }
                },
            }),
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'RENAME',
                onclick: () => rename(),
            }),
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                text: 'DELETE',
                onclick: () => remove(),
            }),
        );
    }

    function renderItems() {
        if (items.length === 0) {
            body.replaceChildren(el('div', { class: 'notice' },
                el('p', { text: 'THIS PLAYLIST IS EMPTY.' }),
                el('p', {
                    class: 'view-header__count',
                    text: 'Add tracks from the library using the ADD TO PLAYLIST button.',
                }),
                el('a', { class: 'btn', href: '/library', 'data-route': '/library', text: 'BROWSE THE LIBRARY' })));
            return;
        }

        const list = el('ol', { class: 'queue', 'aria-label': `Tracks in ${title}` });

        for (const [index, item] of items.entries()) {
            list.append(row(item, index));
        }

        body.replaceChildren(list);
    }

    function row(item, index) {
        const label = `${item.title}${item.artist ? ` by ${item.artist}` : ''}`;

        const play = el('button', {
            class: 'queue__play',
            type: 'button',
            disabled: !item.isAvailable,
            'aria-label': item.isAvailable ? `Play ${label}` : `${label} is unavailable`,

            // The offset is counted among *available* items, because that is what
            // /tracks returns. Passing the display index would start the wrong track on any
            // playlist containing a trashed file -- one position out per unavailable entry
            // above it.
            onclick: () => playPlaylist(player, playlistId, title, availableBefore(index)),
        },
            el('span', { class: 'queue__title', text: item.title }),
            el('span', { class: 'queue__artist', text: item.artist ?? '' }));

        const duration = el('time', { class: 'queue__duration', text: mmss(item.durationSeconds) });
        const iso = isoDuration(item.durationSeconds);
        if (iso) duration.setAttribute('datetime', iso);

        const up = el('button', {
            class: 'icon-btn icon-btn--sm',
            type: 'button',
            disabled: index === 0,
            'data-move': 'up',
            'aria-label': `Move ${item.title} up`,
            onclick: () => move(index, index - 1),
        }, el('span', { 'aria-hidden': 'true', text: '↑' }));

        const down = el('button', {
            class: 'icon-btn icon-btn--sm',
            type: 'button',
            disabled: index === items.length - 1,
            'data-move': 'down',
            'aria-label': `Move ${item.title} down`,
            onclick: () => move(index, index + 1),
        }, el('span', { 'aria-hidden': 'true', text: '↓' }));

        const drop = el('button', {
            class: 'icon-btn icon-btn--sm',
            type: 'button',
            'aria-label': `Remove ${item.title} from ${title}`,
            onclick: () => removeItem(item),
        }, el('span', { 'aria-hidden': 'true', text: '✕' }));

        const node = el('li', { class: 'queue__row' }, play, duration, up, down, drop);

        if (!item.isAvailable) node.dataset.unavailable = '1';

        return node;
    }

    /** How many playable items precede `index`, which is the offset into the track-id list. */
    function availableBefore(index) {
        return items.slice(0, index).filter((item) => item.isAvailable).length;
    }

    async function move(from, to) {
        if (to < 0 || to >= items.length) return;

        const reordered = [...items];
        const [moved] = reordered.splice(from, 1);
        reordered.splice(to, 0, moved);

        // Applied locally first so the list does not visibly lag behind the button press, then
        // reconciled with whatever the server says. A failed reorder reloads, which puts the
        // real order back.
        items = reordered;
        renderItems();

        // Focus follows the row that moved. Without this, a keyboard user pressing "move down"
        // repeatedly loses the button after the first press, because the element they were on
        // has just been replaced by a re-render.
        const direction = to > from ? 'down' : 'up';
        const rows = body.querySelectorAll('.queue__row');
        const wanted = rows[to]?.querySelector(`[data-move="${direction}"]`);

        // At either end the button in that direction is disabled and cannot take focus, so fall
        // back to the opposite one rather than dropping focus to the body.
        const fallback = rows[to]?.querySelector(`[data-move="${direction === 'up' ? 'down' : 'up'}"]`);

        (wanted?.disabled ? fallback : wanted)?.focus();

        try {
            await api.reorderPlaylist(playlistId, items.map((item) => item.itemId));
            announce(`${moved.title} moved to position ${to + 1} of ${items.length}.`);
        } catch (error) {
            alertUser(error.message);
            await load();
        }
    }

    async function removeItem(item) {
        try {
            await api.removePlaylistItem(playlistId, item.itemId);
            announce(`${item.title} removed from ${title}.`);
            await load();
        } catch (error) {
            alertUser(error.message);
        }
    }

    async function rename() {
        // A native prompt rather than an inline editor or a custom modal. It is focus-trapped,
        // labelled and dismissible for free, which a hand-rolled dialog only becomes after real
        // work -- and this app has no dialog primitive yet.
        const next = window.prompt('New name for this playlist', title);

        if (next === null) return;

        const trimmed = next.trim();

        if (trimmed.length === 0) {
            alertUser('A playlist needs a name.');
            return;
        }

        try {
            await api.updatePlaylist(playlistId, { name: trimmed });
            announce(`Renamed to ${trimmed}.`);
            await load();
        } catch (error) {
            alertUser(error.message);
        }
    }

    async function remove() {
        // Confirmed, because deleting a playlist cannot be undone -- there is no trash for these,
        // and the tracks themselves are untouched so the loss is exactly the curation.
        if (!window.confirm(`Delete the playlist "${title}"? This cannot be undone.`)) return;

        try {
            await api.deletePlaylist(playlistId);
            announce(`${title} deleted.`);

            // A full navigation is wrong here; the router owns /playlists.
            window.history.pushState({}, '', '/playlists');
            window.dispatchEvent(new PopStateEvent('popstate'));
        } catch (error) {
            alertUser(error.message);
        }
    }

    load();

    return () => controller.abort();
}

/** Queues a playlist and starts it, from `startAt` if given. */
async function playPlaylist(player, playlistId, name, startAt = 0) {
    try {
        const ids = await api.playlistTrackIds(playlistId);

        if (ids.length === 0) {
            alertUser(`${name} has nothing that can be played.`);
            return;
        }

        if (player.playAll(ids, { startAt })) announce(`Playing ${name}.`);
    } catch (error) {
        alertUser(error.message);
    }
}
