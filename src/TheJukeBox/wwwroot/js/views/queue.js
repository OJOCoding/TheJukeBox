// The play queue, made visible.
//
// The nav has linked to /queue since B2 with nothing behind it, so the link silently redirected
// to the library. This is the view it was always pointing at.
//
// Rendered as an ordered list of rows with buttons, not as a listbox. A listbox may only contain
// `option` elements and an option may not contain interactive children, so the per-row remove
// button rules it out -- and the library's roving-activedescendant pattern cannot host controls
// either. The honest alternatives were a `role="grid"` with two-dimensional keyboard navigation,
// or a plain list. A plain list wins here: the queue is short by nature, every browser and
// screen reader handles it correctly with no custom key handling, and a grid's cost is paid in
// the one place a subtle ARIA mistake is hardest to notice.

import { alertUser, announce, el, isoDuration, mmss } from '../core/ui.js';
import { createAdaptiveList } from '../ui/virtual-list.js';

/** Must match --queue-row-h in tokens.css: windowed rows are positioned arithmetically. */
const ROW_HEIGHT = 72;

export function renderQueue(outlet, { store, player }) {
    const count = el('p', { class: 'view-header__count', text: '' });

    const list = el('ol', { class: 'queue', 'aria-label': 'Play queue' });

    const clearButton = el('button', {
        class: 'btn btn--sm',
        type: 'button',
        text: 'CLEAR QUEUE',
        onclick: () => {
            player.clearQueue();
            render();
        },
    });

    // A stable container swapped between the list and the empty notice. Replacing the list
    // element itself would leave the notice behind the next time the queue filled up.
    const body = el('div', {}, list);

    outlet.append(
        el(
            'div',
            { class: 'view-header' },
            el('h2', { text: 'QUEUE' }),
            count,
            clearButton,
        ),
        body,
    );

    // Windowed above the same threshold as the library. A queue is usually short, which is why
    // it was never windowed -- but "play everything" on a large library builds one that is not,
    // and the two-thousand rows that produced were four controls each.
    const rows = createAdaptiveList({ container: list, scrollParent: outlet, rowHeight: ROW_HEIGHT });

    /** The entries the current render was built from, read back by lazily built rows. */
    let entries = [];

    function render() {
        entries = player.queueEntries();

        count.textContent = entries.length === 0
            ? 'EMPTY'
            : `${entries.length} TRACK${entries.length === 1 ? '' : 'S'}`;

        clearButton.disabled = entries.length === 0;

        if (entries.length === 0) {
            rows.render(0, buildRow);
            body.replaceChildren(emptyNotice());
            return;
        }

        body.replaceChildren(list);
        rows.render(entries.length, buildRow);
    }

    /**
     * Builds the row at a display index.
     *
     * Reads `entries` rather than closing over one, because when windowed this is called long
     * after render() returned -- on every scroll -- and a captured array would go stale the
     * first time the queue changed.
     */
    function buildRow(index) {
        return row(entries[index]);
    }

    function emptyNotice() {
        return el(
            'div',
            { class: 'notice' },
            el('p', { text: 'THE QUEUE IS EMPTY.' }),
            el('a', { class: 'btn', href: '/library', 'data-route': '/library', text: 'BROWSE THE LIBRARY' }),
        );
    }

    function row(entry) {
        const track = entry.track;

        // A queued id with no catalogue entry: the library was refreshed and this track is gone.
        // Reconciliation removes these, but a render can happen in between.
        const title = track?.title ?? 'UNAVAILABLE TRACK';

        const play = el('button', {
            class: 'queue__play',
            type: 'button',

            // The accessible name carries what the button does *and* to what, because a screen
            // reader user tabbing through a queue hears the names in isolation.
            'aria-label': `Play ${title}${track?.artist ? ` by ${track.artist}` : ''}`,
            onclick: () => {
                player.jumpToPosition(entry.position);
                render();
            },
        },
            el('span', { class: 'queue__title', text: title }),
            el('span', { class: 'queue__artist', text: track?.artist ?? '' }));

        const duration = el('time', { class: 'queue__duration', text: mmss(track?.durationSeconds) });
        const iso = isoDuration(track?.durationSeconds);
        if (iso) duration.setAttribute('datetime', iso);

        const remove = el('button', {
            class: 'icon-btn icon-btn--sm',
            type: 'button',
            'aria-label': `Remove ${title} from the queue`,
            onclick: () => {
                player.removeAt(entry.position);
                announce(`${title} removed from the queue.`);
                render();
            },
        }, el('span', { 'aria-hidden': 'true', text: '✕' }));

        const item = el('li', { class: 'queue__row' }, play, duration, remove);

        if (entry.isCurrent) {
            // aria-current, not just a class: "which one is playing" has to be available to a
            // screen reader, not only to a sighted user.
            item.setAttribute('aria-current', 'true');
        }

        if (!track) item.dataset.unavailable = '1';

        return item;
    }

    // Re-rendered on queue changes, not on every store update: the position ticker fires several
    // times a second and rebuilding the list that often would fight the user's own clicks.
    let lastRevision = store.state.queueRevision;

    const unsubscribe = store.subscribe((state) => {
        if (state.queueRevision === lastRevision) return;

        lastRevision = state.queueRevision;
        render();
    });

    render();

    if (entries.length === 0) {
        // Announced rather than silent, so a keyboard user who navigated here is told why the
        // view has nothing in it.
        announce('The queue is empty.');
    }

    return () => {
        unsubscribe();

        // Or navigating away leaves a renderer observing the shell's scroll region on behalf of
        // a list that no longer exists.
        rows.destroy();
    };
}

/** Adds a whole list of tracks to the queue, reporting what happened. */
export function enqueueAll(player, trackIds) {
    const added = player.enqueue(trackIds);

    if (added === 0) alertUser('Nothing here could be added to the queue.');

    return added;
}
