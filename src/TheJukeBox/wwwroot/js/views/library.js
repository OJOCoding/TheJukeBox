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
import { createAdaptiveList } from '../ui/virtual-list.js';
import { addTracksToPlaylist } from '../ui/playlist-picker.js';

/** Must match --row-h in app.css: windowed rows are positioned arithmetically. */
const ROW_HEIGHT = 56;

/**
 * The orderings the server supports, with names for people.
 *
 * `/api/tracks?sort=` has accepted all seven of these since B1 and nothing ever offered them, so
 * a library could only ever be read alphabetically. "Recently added" and "most played" are the
 * two orderings a music library is actually for.
 *
 * The values are the wire contract -- see LibraryEndpoints.Order -- so they are not free to be
 * prettier than this.
 */
const SORTS = [
    { value: 'title', label: 'TITLE' },
    { value: 'artist', label: 'ARTIST' },
    { value: 'album', label: 'ALBUM' },
    { value: 'added', label: 'RECENTLY ADDED' },
    { value: 'recent', label: 'RECENTLY PLAYED' },
    { value: 'played', label: 'MOST PLAYED' },
    { value: 'duration', label: 'DURATION' },
];

/**
 * The two orderings that are *about* listening, so the play count earns its column there.
 *
 * Shown conditionally rather than always. A count of zero beside every track is noise while you
 * are looking for a song by name, and it is the entire point when you have asked for the ones
 * you play most.
 */
const LISTENING_SORTS = new Set(['played', 'recent']);

/** Where the chosen ordering is remembered between visits. */
const SORT_KEY = 'jukebox.library.sort';

function storedSort() {
    try {
        const saved = window.localStorage.getItem(SORT_KEY);

        // Validated against the list rather than trusted: localStorage is user-writable, and an
        // unrecognised value would be sent to the server and silently fall back to title anyway,
        // leaving the control disagreeing with the list it produced.
        return SORTS.some((s) => s.value === saved) ? saved : 'title';
    } catch {
        // Storage can throw outright when cookies are blocked. A default is a fine answer.
        return 'title';
    }
}

function rememberSort(value) {
    try {
        window.localStorage.setItem(SORT_KEY, value);
    } catch {
        // Not worth telling anyone about: the sort still applies for this visit.
    }
}

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

    // Acts on the selected row, which is the list's cursor. A per-row menu button was rejected:
    // it would put two more tab stops on every one of potentially thousands of rows, which is
    // exactly the problem the listbox pattern was chosen to avoid.
    //
    // Which row that is has to be *visible*, though. The buttons used to say "ADD TO QUEUE" with
    // no indication of what they would add, so the only way to find out was to press one.
    const selectedLabel = el('span', { class: 'view-header__selected', text: '' });

    const playButton = el('button', {
        class: 'btn btn--sm',
        type: 'button',
        text: 'PLAY',
        onclick: () => {
            if (tracks[selected]) activate(selected);
            else alertUser('Select a track first.');
        },
    });

    const queueButton = el('button', {
        class: 'btn btn--sm',
        type: 'button',
        text: 'ADD TO QUEUE',
        onclick: () => {
            const track = tracks[selected];

            if (!track) {
                alertUser('Select a track first.');
                return;
            }

            // One track, and it does not disturb what is playing. Contrast activate(), which
            // replaces the queue with the whole view.
            player.enqueue([track.id]);
        },
    });

    const playlistButton = el('button', {
        class: 'btn btn--sm',
        type: 'button',
        text: 'ADD TO PLAYLIST',
        onclick: () => {
            const track = tracks[selected];

            if (!track) alertUser('Select a track first.');
            else void addTracksToPlaylist([track.id], track.title);
        },
    });

    const sort = el('select', {
        class: 'select',
        id: 'librarySort',
        'aria-label': 'Order the library by',
    }, ...SORTS.map((option) => el('option', { value: option.value, text: option.label })));

    sort.value = storedSort();

    sort.addEventListener('change', () => {
        rememberSort(sort.value);

        // Selection is an index into the old order, so keeping it would leave the cursor on a
        // different track than the one it was on. load() resets it to the top.
        load(search.value.trim());
    });

    const playAllButton = el('button', {
        class: 'btn btn--sm',
        type: 'button',
        text: 'PLAY ALL',
        onclick: () => {
            if (tracks.length === 0) {
                alertUser('There is nothing here to play.');
                return;
            }

            player.playAll(tracks.map((track) => track.id), { startAt: Math.max(selected, 0) });
        },
    });

    outlet.append(
        el(
            'div',
            { class: 'view-header' },
            el('h2', { text: 'LIBRARY' }),
            count,
            el('form', { class: 'field', role: 'search', onsubmit: (e) => e.preventDefault() },
                el('span', { class: 'field__prompt', 'aria-hidden': 'true', text: '>' }),
                search),
            el('label', { class: 'field field--select', htmlFor: 'librarySort' },
                el('span', { class: 'field__label', text: 'SORT' }),
                sort),
            el('div', { class: 'view-header__actions' },
                playButton, queueButton, playlistButton, playAllButton),
            selectedLabel,
        ),
        body,
    );

    /** @type {Array<object>} the rows currently rendered, in display order. */
    let tracks = [];
    let selected = -1;
    let searchTimer = null;
    let typeAhead = { text: '', at: 0 };

    // The shell's single scroll region, which is what actually scrolls. The list does not
    // scroll itself: B2 deliberately avoided a nested scroll area.
    const scrollParent = outlet;

    const rows = createAdaptiveList({ container: list, scrollParent, rowHeight: ROW_HEIGHT });

    function rowId(index) {
        return `track-${index}`;
    }

    /** Builds one row. Called for every track when small, per visible index when windowed. */
    function buildRow(index) {
        const track = tracks[index];
        const row = fromTemplate('tpl-track-row');

        row.id = rowId(index);
        row.dataset.trackId = track.id;

        // The index is carried on the element rather than inferred from its position among
        // siblings. Under virtualisation the DOM holds only a window, so sibling position and
        // track index are unrelated, and reading one for the other plays the wrong song.
        row.dataset.index = String(index);

        row.setAttribute('aria-setsize', String(tracks.length));
        row.setAttribute('aria-posinset', String(index + 1));

        row.querySelector('.list-row__num').textContent = String(index + 1);
        row.querySelector('.list-row__title').textContent = track.title;
        row.querySelector('.list-row__artist').textContent = track.artist ?? '';
        row.querySelector('.list-row__album').textContent = track.album ?? '';

        const duration = row.querySelector('.list-row__duration');
        duration.textContent = mmss(track.durationSeconds);

        // Duration, not file size. The old player put escapeHtml(track.size) into an element
        // literally classed .track-duration, so the design's mm:ss slot displayed bytes.
        const iso = isoDuration(track.durationSeconds);
        if (iso) duration.setAttribute('datetime', iso);

        // Filled whichever ordering is showing, because the cell is hidden by CSS rather than
        // omitted -- writing it unconditionally keeps buildRow free of the sort.
        const plays = row.querySelector('.list-row__plays');
        const count = track.playCount ?? 0;

        plays.textContent = count === 0 ? '—' : String(count);
        plays.setAttribute('aria-label', count === 1 ? '1 play' : `${count} plays`);

        return row;
    }

    function render() {
        if (tracks.length === 0) {
            rows.render(0, buildRow);
            body.replaceChildren(el('p', { class: 'notice', text: 'NO TRACKS MATCH.' }));
            return;
        }

        body.replaceChildren(list);

        // The row height must match the stylesheet's --row-h exactly when windowed, since rows
        // are then positioned arithmetically rather than by flow.
        rows.render(tracks.length, buildRow);

        paintStates();
    }

    /** Applies selection and now-playing state without rebuilding rows. */
    function paintStates() {
        paintSelection();

        const playingId = store.state.currentTrackId;

        for (const row of list.children) {
            const isSelected = Number(row.dataset.index) === selected;

            // aria-selected is the list cursor; aria-current is what is playing. They are
            // different states and the stylesheet keys off both, so the visible state and
            // the announced state cannot drift.
            row.setAttribute('aria-selected', isSelected ? 'true' : 'false');

            if (row.dataset.trackId === playingId) row.setAttribute('aria-current', 'true');
            else row.removeAttribute('aria-current');

            if (player.isUnplayable(row.dataset.trackId)) row.dataset.unplayable = '1';
        }

        // Only set when the row exists. aria-activedescendant pointing at an id that is not
        // in the DOM is worse than not setting it: assistive technology reports nothing and
        // the list appears to have no cursor at all.
        if (selected >= 0 && list.querySelector(`#${rowId(selected)}`)) {
            list.setAttribute('aria-activedescendant', rowId(selected));
        } else {
            list.removeAttribute('aria-activedescendant');
        }
    }

    /** Keeps the header's "acting on this" label in step with the cursor. */
    function paintSelection() {
        const track = tracks[selected];

        selectedLabel.textContent = track
            ? `SELECTED: ${track.title}${track.artist ? ` — ${track.artist}` : ''}`
            : tracks.length > 0 ? 'SELECT A TRACK' : '';

        const none = !track;

        playButton.disabled = none;
        queueButton.disabled = none;
        playlistButton.disabled = none;
        playAllButton.disabled = tracks.length === 0;
    }

    function focusIndex(next) {
        if (tracks.length === 0) return;

        selected = Math.min(Math.max(next, 0), tracks.length - 1);

        // Revealed before painting, not after: when windowed this is what renders the target
        // row, and aria-activedescendant must point at an id that already exists.
        rows.reveal(selected);
        paintStates();
    }

    function activate(index) {
        const track = tracks[index];
        if (!track) return;

        player.playTrack(track.id, tracks.map((t) => t.id));
    }

    // Delegated: one listener for the whole list rather than a handler per row, which is
    // what the previous inline onclick attributes amounted to.
    //
    // A single click *selects*; playing takes a double-click or Enter. That split exists because
    // playing a track queues the rest of the view behind it, so when click also played, there was
    // no way to point at a track without replacing the queue -- "add one song to the queue" put
    // the whole library in it, because selecting the song had already done that. Select-then-act is
    // also the standard behaviour for a listbox, which this is.
    list.addEventListener('click', (event) => {
        const index = indexOfRow(event.target);

        if (index !== null) focusIndex(index);
    });

    list.addEventListener('dblclick', (event) => {
        const index = indexOfRow(event.target);

        if (index !== null) {
            focusIndex(index);
            activate(index);
        }
    });

    /** The track index a click landed on, or null. */
    function indexOfRow(target) {
        const row = target instanceof Element ? target.closest('.list-row') : null;

        if (!row) return null;

        // From the element, not its sibling position: under virtualisation those differ.
        const index = Number(row.dataset.index);

        return Number.isInteger(index) ? index : null;
    }

    list.addEventListener('keydown', (event) => {
        // Page size from the scroll viewport, not the list. The list element is now as tall
        // as the whole library when windowed, so measuring it would page by thousands.
        const rows = Math.max(1, Math.floor(scrollParent.clientHeight / ROW_HEIGHT) - 1);
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
            const page = await api.tracks({ q: query, sort: sort.value, signal: controller.signal });

            tracks = page.items;
            selected = tracks.length > 0 ? 0 : -1;

            // The play-count column appears only for the orderings it explains. Set on the list
            // rather than per row so the grid template changes once, not once per track.
            list.classList.toggle('list--plays', LISTENING_SORTS.has(sort.value));

            count.textContent = query
                ? `${page.total} MATCHING`
                : `${page.total} TRACK${page.total === 1 ? '' : 'S'}`;

            // Merged, not replaced. This list is one page of one search, so treating it as the
            // whole library is what used to prune the queue on every keystroke.
            player.mergeCatalogue(tracks);
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

        // Detaches the scroll and resize listeners. Without this, navigating away leaves a
        // renderer observing the shell's scroll region for a list that no longer exists.
        rows.destroy();
    };
}
