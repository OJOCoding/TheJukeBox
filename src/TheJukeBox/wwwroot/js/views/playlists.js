// The playlists index, and the playlist detail view.
//
// Reordering works two ways, and the buttons came first on purpose. Drag-and-drop alone is
// unusable by keyboard and does not fire at all from a touchscreen -- HTML5 drag events have no
// touch equivalent -- so move-up/move-down is the implementation and dragging is an accelerator
// layered over it. Both call the same move(), so there is one reorder to get right, not two.

import { alertUser, announce, clear, el, isoDuration, mmss, notify } from '../core/ui.js';
import { ApiError, api } from '../core/api.js';
import { askForText, askToConfirm } from '../ui/dialog.js';
import { createAdaptiveList } from '../ui/virtual-list.js';

/** Must match --queue-row-h in tokens.css: windowed rows are positioned arithmetically. */
const ROW_HEIGHT = 72;

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
                notify(`Created the playlist ${playlist.name}.`);

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
                        text: 'Name one above. Then in the library, select a track and press '
                            + 'ADD TO PLAYLIST — that button will also offer to make a playlist '
                            + 'for you.',
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
            // The way in from here. Adding tracks happens in the library, an album or an artist
            // page -- that is where the tracks are -- but nothing on this screen said so, so an
            // empty playlist was a dead end you had to guess your way out of.
            //
            // There were two of these buttons, side by side, saying the same thing and going to
            // the same place: one was added, then added again by someone reading the screen
            // rather than the file.
            el('a', {
                class: 'btn btn--sm',
                href: '/library',
                'data-route': '/library',
                text: 'ADD TRACKS',
                'aria-label': `Add tracks to ${title} from the library`,
                title: 'Pick a track in the library, then use ADD TO PLAYLIST',
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

    // One list element for the life of the view, so the windowed renderer keeps a stable
    // container to attach to across reorders.
    const list = el('ol', { class: 'queue', 'aria-label': 'Playlist tracks' });

    const rows = createAdaptiveList({ container: list, scrollParent: outlet, rowHeight: ROW_HEIGHT });

    function renderItems() {
        if (items.length === 0) {
            rows.render(0, buildRow);
            body.replaceChildren(el('div', { class: 'notice' },
                el('p', { text: 'THIS PLAYLIST IS EMPTY.' }),
                el('p', {
                    class: 'view-header__count',
                    text: 'Select a track in the library — or open an album or an artist — and '
                        + 'press ADD TO PLAYLIST.',
                }),
                el('a', { class: 'btn', href: '/library', 'data-route': '/library', text: 'BROWSE THE LIBRARY' })));
            return;
        }

        list.setAttribute('aria-label', `Tracks in ${title}`);

        body.replaceChildren(list);
        rows.render(items.length, buildRow);
    }

    /**
     * Builds the row at an index.
     *
     * Reads `items` rather than closing over it: when windowed this runs on every scroll, long
     * after renderItems returned, and a captured array would go stale on the first reorder.
     */
    function buildRow(index) {
        return row(items[index], index);
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

        // A visible affordance for the drag, and nothing more: it is aria-hidden and not
        // focusable, because dragging has no keyboard equivalent and the move buttons beside it
        // are the accessible path. Advertising a control that keyboard users cannot operate
        // would be worse than leaving the drag undiscoverable.
        const grip = el('span', { class: 'queue__grip', 'aria-hidden': 'true', text: '⠿' });

        const node = el('li', { class: 'queue__row queue__row--draggable', draggable: true },
            grip, play, duration, up, down, drop);

        // Read back by the drag handlers, which are delegated to the list. Under windowing a
        // row's position among its siblings is unrelated to its position in the playlist, so
        // the index has to travel on the element.
        node.dataset.index = String(index);

        if (!item.isAvailable) node.dataset.unavailable = '1';

        return node;
    }

    // ---------------------------------------------------------------------
    // Dragging
    // ---------------------------------------------------------------------
    //
    // Delegated to the list rather than bound per row: a playlist can be windowed, so rows come
    // and go under the pointer, and a listener attached at build time would be attached to a
    // row that has since been recycled.

    /** The index being dragged, or -1. */
    let dragging = -1;

    function indexOf(target) {
        const row = target instanceof Element ? target.closest('.queue__row') : null;
        const index = row ? Number(row.dataset.index) : NaN;

        return Number.isInteger(index) ? index : -1;
    }

    list.addEventListener('dragstart', (event) => {
        dragging = indexOf(event.target);

        if (dragging < 0) return;

        // Firefox starts no drag at all unless some data is set. The payload itself is unused --
        // the index is held here, because a drop from another window is not a reorder.
        event.dataTransfer.setData('text/plain', String(dragging));
        event.dataTransfer.effectAllowed = 'move';

        rows.nodeAt(dragging)?.setAttribute('data-dragging', 'true');
    });

    list.addEventListener('dragover', (event) => {
        if (dragging < 0) return;

        // Without preventDefault the browser refuses the drop: the default action for dragover
        // is "this is not a valid target".
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';

        const over = event.target instanceof Element ? event.target.closest('.queue__row') : null;

        clearDropMarkers();

        if (!over || over.dataset.index === String(dragging)) return;

        // Above or below the midpoint, which is what makes the line land where the row will.
        const box = over.getBoundingClientRect();
        over.dataset.drop = event.clientY < box.top + box.height / 2 ? 'before' : 'after';
    });

    list.addEventListener('drop', (event) => {
        // Captured before endDrag, which resets it. Reading `dragging` afterwards gave -1, and
        // -1 is a legal argument to splice that means "from the end" -- so every drop silently
        // reordered the *last* row instead of the one under the pointer.
        const from = dragging;

        if (from < 0) return;

        event.preventDefault();

        const over = event.target instanceof Element ? event.target.closest('.queue__row') : null;
        const target = over ? Number(over.dataset.index) : NaN;
        const before = over?.dataset.drop !== 'after';

        endDrag();

        if (!Number.isInteger(target) || target === from) return;

        // Converted from "insert before/after row N" to the index the item ends up at once it
        // has been spliced out. Removing it first shifts everything below it up by one, so a
        // downward move overshoots by exactly that one place without this.
        let to = before ? target : target + 1;
        if (from < to) to -= 1;

        if (from !== to) void move(from, to);
    });

    // dragend fires whether or not a drop landed, which is what clears the markers after a drag
    // that was abandoned outside the list.
    list.addEventListener('dragend', endDrag);

    function endDrag() {
        clearDropMarkers();

        for (const node of list.querySelectorAll('[data-dragging]')) node.removeAttribute('data-dragging');

        dragging = -1;
    }

    function clearDropMarkers() {
        for (const node of list.querySelectorAll('[data-drop]')) delete node.dataset.drop;
    }

    /** How many playable items precede `index`, which is the offset into the track-id list. */
    function availableBefore(index) {
        return items.slice(0, index).filter((item) => item.isAvailable).length;
    }

    async function move(from, to) {
        // Both ends validated, not just the destination. splice() accepts a negative index as
        // an offset from the end rather than rejecting it, so an out-of-range `from` does not
        // throw -- it quietly moves a different track, which is the worst of both worlds.
        if (from < 0 || from >= items.length) return;
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
        //
        // Asked for by index rather than read off the DOM: when windowed the nth child of the
        // list is not the nth item, so `querySelectorAll(...)[to]` would focus a control on some
        // other track -- and the next press would move that one.
        const landed = rows.nodeAt(to);
        const direction = to > from ? 'down' : 'up';
        const wanted = landed?.querySelector(`[data-move="${direction}"]`);

        // At either end the button in that direction is disabled and cannot take focus, so fall
        // back to the opposite one rather than dropping focus to the body.
        const fallback = landed?.querySelector(`[data-move="${direction === 'up' ? 'down' : 'up'}"]`);

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
            notify(`Removed "${item.title}" from ${title}.`);
            await load();
        } catch (error) {
            alertUser(error.message);
        }
    }

    async function rename() {
        const next = await askForText({
            title: 'RENAME PLAYLIST',
            value: title,
            confirmLabel: 'RENAME',
        });

        if (next === null) return;

        try {
            await api.updatePlaylist(playlistId, { name: next });
            notify(`Renamed to ${next}.`);
            await load();
        } catch (error) {
            alertUser(error.message);
        }
    }

    async function remove() {
        // Confirmed, because deleting a playlist cannot be undone -- there is no trash for these,
        // and the tracks themselves are untouched so the loss is exactly the curation.
        const confirmed = await askToConfirm({
            title: 'DELETE PLAYLIST',
            message: `Delete "${title}"? This cannot be undone. The tracks themselves are not touched.`,
            confirmLabel: 'DELETE',
        });

        if (!confirmed) return;

        try {
            await api.deletePlaylist(playlistId);
            notify(`Deleted the playlist ${title}.`);

            // A full navigation is wrong here; the router owns /playlists.
            window.history.pushState({}, '', '/playlists');
            window.dispatchEvent(new PopStateEvent('popstate'));
        } catch (error) {
            alertUser(error.message);
        }
    }

    load();

    return () => {
        controller.abort();
        rows.destroy();
    };
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
