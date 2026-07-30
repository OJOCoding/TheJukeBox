// The library view.
//
// The track list is a listbox, not a set of buttons and not a pile of divs. The old
// markup rendered rows as `<div onclick=...>` with no tabindex, role or key handler, so a
// keyboard user could not select a track at all -- while player.css carried a
// `.track-item:focus` rule that could never match, which is fairly clear evidence the
// author intended otherwise.
//
// A button per row was rejected: with thousands of tracks that is thousands of tab stops
// and no way past them. role="listbox" with a roving aria-activedescendant is one tab
// stop, and aria-setsize/aria-posinset are designed for exactly this.

import { alertUser, announce, clear, el, fromTemplate, isoDuration, mmss } from '../core/ui.js';
import { api, ApiError } from '../core/api.js';

export function renderLibrary(outlet, { store, player, onSync }) {
    const controller = new AbortController();

    const count = el('span', { class: 'view-header__count', text: '' });
    const search = el('input', {
        type: 'search',
        id: 'librarySearch',
        placeholder: 'SEARCH LIBRARY',
        'aria-label': 'Search the library',
        autocomplete: 'off',
    });

    const list = el('ul', {
        class: 'list',
        id: 'trackList',
        role: 'listbox',
        tabIndex: 0,
        'aria-label': 'Tracks',
    });

    const body = el('div', {}, list);

    outlet.append(
        el(
            'div',
            { class: 'view-header' },
            el('h2', { text: 'LIBRARY' }),
            count,
            el('form', { class: 'field', role: 'search', onsubmit: (e) => e.preventDefault() },
                el('span', { class: 'field__prompt', 'aria-hidden': 'true', text: '>' }),
                search),
        ),
        body,
    );

    /** @type {Array<object>} the rows currently rendered, in display order. */
    let tracks = [];
    let selected = -1;
    let searchTimer = null;
    let typeAhead = { text: '', at: 0 };

    function rowId(index) {
        return `track-${index}`;
    }

    function render() {
        clear(list);

        if (tracks.length === 0) {
            body.replaceChildren(el('p', { class: 'notice', text: 'NO TRACKS MATCH.' }));
            return;
        }

        const fragment = document.createDocumentFragment();

        tracks.forEach((track, index) => {
            const row = fromTemplate('tpl-track-row');

            row.id = rowId(index);
            row.dataset.trackId = track.id;
            row.setAttribute('aria-setsize', String(tracks.length));
            row.setAttribute('aria-posinset', String(index + 1));

            row.querySelector('.list-row__num').textContent = String(index + 1);
            row.querySelector('.list-row__title').textContent = track.title;
            row.querySelector('.list-row__artist').textContent = track.artist ?? '';
            row.querySelector('.list-row__album').textContent = track.album ?? '';

            const duration = row.querySelector('.list-row__duration');
            duration.textContent = mmss(track.durationSeconds);

            // Duration, not file size. The old player put escapeHtml(track.size) into an
            // element literally classed .track-duration, so the design's mm:ss slot
            // displayed bytes. B1 fills durationSeconds from embedded tags; until then
            // this correctly reads "--:--" rather than lying.
            const iso = isoDuration(track.durationSeconds);
            if (iso) duration.setAttribute('datetime', iso);

            fragment.append(row);
        });

        body.replaceChildren(list);
        list.append(fragment);

        paintStates();
    }

    /** Applies selection and now-playing state without re-rendering rows. */
    function paintStates() {
        const playingId = store.state.currentTrackId;

        for (const row of list.children) {
            const isSelected = row.id === rowId(selected);

            // aria-selected is the list cursor; aria-current is what is playing. They are
            // different states and the stylesheet keys off both, so the visible state and
            // the announced state cannot drift.
            row.setAttribute('aria-selected', isSelected ? 'true' : 'false');

            if (row.dataset.trackId === playingId) row.setAttribute('aria-current', 'true');
            else row.removeAttribute('aria-current');

            if (player.isUnplayable(row.dataset.trackId)) row.dataset.unplayable = '1';
        }

        if (selected >= 0) list.setAttribute('aria-activedescendant', rowId(selected));
        else list.removeAttribute('aria-activedescendant');
    }

    function focusIndex(next) {
        if (tracks.length === 0) return;

        selected = Math.min(Math.max(next, 0), tracks.length - 1);
        paintStates();

        list.children[selected]?.scrollIntoView({ block: 'nearest' });
    }

    function activate(index) {
        const track = tracks[index];
        if (!track) return;

        player.playTrack(track.id, tracks.map((t) => t.id));
    }

    // Delegated: one listener for the whole list rather than a handler per row, which is
    // what the previous inline onclick attributes amounted to.
    list.addEventListener('click', (event) => {
        const row = event.target.closest('.list-row');
        if (!row) return;

        const index = [...list.children].indexOf(row);
        focusIndex(index);
        activate(index);
    });

    list.addEventListener('keydown', (event) => {
        const rows = Math.max(1, Math.floor(list.clientHeight / 56) - 1);
        let next = null;

        switch (event.key) {
            case 'ArrowDown': next = selected + 1; break;
            case 'ArrowUp': next = selected - 1; break;
            case 'PageDown': next = selected + rows; break;
            case 'PageUp': next = selected - rows; break;
            case 'Home': next = 0; break;
            case 'End': next = tracks.length - 1; break;
            case 'Enter':
                event.preventDefault();
                activate(selected);
                return;
            default: {
                // Type-ahead: jump to the next title starting with what was typed.
                if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey) return;

                const now = Date.now();
                typeAhead.text = now - typeAhead.at > 800 ? event.key : typeAhead.text + event.key;
                typeAhead.at = now;

                const needle = typeAhead.text.toLowerCase();
                const found = tracks.findIndex((t) => t.title.toLowerCase().startsWith(needle));

                if (found >= 0) {
                    event.preventDefault();
                    focusIndex(found);
                }
                return;
            }
        }

        // Clamped, not wrapped: Home and End exist for jumping to the ends, so wrapping
        // on arrow keys would make the list feel unanchored.
        event.preventDefault();
        focusIndex(next);
    });

    search.addEventListener('input', () => {
        // Debounced, or every keystroke issues a request and the answers race.
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => load(search.value.trim()), 250);
    });

    async function load(query = '') {
        try {
            const page = await api.tracks({ q: query, signal: controller.signal });

            tracks = page.items;
            selected = tracks.length > 0 ? 0 : -1;

            count.textContent = query
                ? `${page.total} MATCHING`
                : `${page.total} TRACK${page.total === 1 ? '' : 'S'}`;

            player.setCatalogue(tracks);
            render();

            if (query) announce(`${page.total} tracks.`);
        } catch (error) {
            if (error.name === 'AbortError') return;

            if (error instanceof ApiError && error.needsDriveConsent) {
                body.replaceChildren(
                    el('div', { class: 'notice notice--error' },
                        el('p', { text: 'DRIVE ACCESS IS REQUIRED.' }),
                        el('a', { class: 'btn', href: '/api/auth/login?returnUrl=/library', text: 'RECONNECT DRIVE' })),
                );
                return;
            }

            body.replaceChildren(
                el('div', { class: 'notice notice--error' },
                    el('p', { text: 'COULD NOT LOAD THE LIBRARY.' }),
                    el('button', { class: 'btn', type: 'button', text: 'RETRY', onclick: () => load(query) }),
                    el('button', { class: 'btn', type: 'button', text: 'SYNC DRIVE', onclick: onSync })),
            );

            alertUser(error.message);
        }
    }

    // Repaint on playback changes so the playing row highlights without re-rendering.
    const unsubscribe = store.subscribe(paintStates);

    load();

    return () => {
        controller.abort();
        clearTimeout(searchTimer);
        unsubscribe();
    };
}
