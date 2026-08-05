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

import { alertUser, announce, clear, el, fromTemplate, isoDuration, mmss, notify } from '../core/ui.js';
import { api, ApiError } from '../core/api.js';
import { VIRTUALISE_ABOVE, createVirtualList } from '../ui/virtual-list.js';

/** Must match --row-h in app.css: windowed rows are positioned arithmetically. */
const ROW_HEIGHT = 56;

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
        onclick: () => addSelectedToPlaylist(),
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
            el('div', { class: 'view-header__actions' },
                playButton, queueButton, playlistButton, playAllButton),
            selectedLabel,
        ),
        body,
    );

    /**
     * Adds the selected track to a playlist the user picks.
     *
     * A native prompt rather than a custom dialog, because this app has no dialog primitive and a
     * hand-rolled one is a focus trap, an escape handler and a scroll lock away from being correct.
     *
     * Creating a playlist is offered from inside the same flow. Without it, someone with no
     * playlists yet was told to go to another screen, make one, come back and find their track
     * again -- four steps to do the thing they had already asked for.
     */
    async function addSelectedToPlaylist() {
        const track = tracks[selected];

        if (!track) {
            alertUser('Select a track first.');
            return;
        }

        let playlists;

        try {
            playlists = await api.playlists();
        } catch (error) {
            alertUser(error.message);
            return;
        }

        const target = playlists.length === 0
            ? await createPlaylistFor(track)
            : await choosePlaylistFor(track, playlists);

        if (!target) return;

        try {
            const result = await api.addToPlaylist(target.id, [track.id]);

            if (result.added === 1) notify(`Added "${track.title}" to ${target.name}.`);
            else alertUser(`"${track.title}" could not be added to ${target.name}.`);
        } catch (error) {
            alertUser(error.message);
        }
    }

    /** Offers to make a first playlist, returning it, or null if the user backed out. */
    async function createPlaylistFor(track) {
        const name = window.prompt(
            `You have no playlists yet.\n\nName one, and "${track.title}" will be added to it:`,
            'Favourites');

        if (name === null) return null;

        const trimmed = name.trim();

        if (trimmed.length === 0) {
            alertUser('A playlist needs a name.');
            return null;
        }

        try {
            const created = await api.createPlaylist(trimmed);

            notify(`Created the playlist ${created.name}.`);

            return created;
        } catch (error) {
            alertUser(error.message);
            return null;
        }
    }

    /** Numbered prompt, with 0 to make a new playlist. */
    async function choosePlaylistFor(track, playlists) {
        const menu = playlists.map((p, index) => `${index + 1}. ${p.name}`).join('\n');

        const answer = window.prompt(
            `Add "${track.title}" to which playlist?\n\n${menu}\n\n0. New playlist…`,
            '1');

        if (answer === null) return null;

        const choice = Number.parseInt(answer.trim(), 10);

        if (choice === 0) return createPlaylistFor(track);

        if (!Number.isInteger(choice) || choice < 1 || choice > playlists.length) {
            alertUser(`"${answer.trim()}" is not one of the listed playlists.`);
            return null;
        }

        return playlists[choice - 1];
    }

    /** @type {Array<object>} the rows currently rendered, in display order. */
    let tracks = [];
    let selected = -1;
    let searchTimer = null;
    let typeAhead = { text: '', at: 0 };

    /** Set only above the threshold; null means every row is in the DOM. */
    let virtual = null;

    // The shell's single scroll region, which is what actually scrolls. The list does not
    // scroll itself: B2 deliberately avoided a nested scroll area.
    const scrollParent = outlet;

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

        return row;
    }

    function render() {
        clear(list);

        virtual?.destroy();
        virtual = null;

        if (tracks.length === 0) {
            body.replaceChildren(el('p', { class: 'notice', text: 'NO TRACKS MATCH.' }));
            return;
        }

        body.replaceChildren(list);

        if (tracks.length > VIRTUALISE_ABOVE) {
            // Windowed. The row height must match the stylesheet's --row-h exactly, since
            // rows are positioned arithmetically rather than by flow.
            virtual = createVirtualList({
                container: list,
                scrollParent,
                rowHeight: ROW_HEIGHT,
                renderRow: buildRow,
            });

            virtual.setTotal(tracks.length);
        } else {
            // Below the threshold every row is rendered. Virtualisation is the riskier path
            // for assistive technology, and most libraries never need it.
            const fragment = document.createDocumentFragment();

            for (let index = 0; index < tracks.length; index++) {
                fragment.append(buildRow(index));
            }

            list.append(fragment);
        }

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

        if (virtual) {
            // Renders the target row before paintStates runs, so activedescendant has
            // something to point at.
            virtual.reveal(selected);
            paintStates();
            return;
        }

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
            const page = await api.tracks({ q: query, signal: controller.signal });

            tracks = page.items;
            selected = tracks.length > 0 ? 0 : -1;

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
        virtual?.destroy();
        virtual = null;
    };
}
