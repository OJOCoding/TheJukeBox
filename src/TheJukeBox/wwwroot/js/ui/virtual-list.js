// Windowed rendering for the track list.
//
// The list previously built one DOM row per track. A few thousand tracks is a few thousand
// rows, each a grid container with five children, and the browser lays out and paints all of
// them on every render -- so scrolling stutters and the first paint after a search is slow.
//
// Only the visible window plus a small overscan is in the DOM. Rows are absolutely positioned
// at index * rowHeight inside a container sized to the full list, which keeps scrollbar
// geometry exactly right without a spacer element. Spacers are the more common technique and
// would be wrong here: role="listbox" permits only option children, and a spacer <li> is not
// an option.

/**
 * Above this many rows a list is windowed; below it every row is rendered.
 *
 * Virtualisation is the riskier path, especially for assistive technology: NVDA's browse-mode
 * buffer can go stale after DOM recycling, and iOS VoiceOver's swipe navigation over a recycled
 * list is historically unreliable. Most real libraries are under this threshold, so most users
 * get the simple, well-understood rendering and only genuinely large ones take on the risk.
 *
 * Declared here rather than at the foot of the file because createAdaptiveList defaults to it.
 */
export const VIRTUALISE_ABOVE = 500;

/**
 * The visible index range, given a scroll position.
 *
 * Pure, and exported separately because every off-by-one in a virtualised list lives here.
 * Rendering the wrong window shows blank space or the wrong rows, and neither is obvious from
 * reading the code.
 *
 * @returns {{first: number, last: number}} inclusive bounds, clamped to the list.
 */
export function computeWindow({ scrollOffset, viewportHeight, rowHeight, total, overscan = 6 }) {
    if (total <= 0 || rowHeight <= 0) return { first: 0, last: -1 };

    // A negative offset means the list starts below the viewport top: the user has not
    // scrolled to it yet, so the window starts at zero rather than at a negative index.
    const offset = Math.max(scrollOffset, 0);

    const firstVisible = Math.floor(offset / rowHeight);

    // Ceil, not floor: a viewport showing two and a half rows shows three.
    const visibleCount = Math.ceil(viewportHeight / rowHeight) + 1;

    // Clamped at both ends. Without the upper clamp, a scroll offset past the end of the list
    // yields first > last -- an inverted window, which the caller's inclusive loop renders as
    // nothing at all, so the list goes blank rather than showing its final rows. The container
    // is only ever as tall as the list, so this needs a programmatic scrollTop to reach; it is
    // still the difference between degrading and breaking.
    const first = Math.min(Math.max(firstVisible - overscan, 0), total - 1);
    const last = Math.min(firstVisible + visibleCount + overscan, total - 1);

    return { first, last: Math.max(last, first) };
}

/**
 * Creates a windowed renderer over a list element.
 *
 * @param options.container    the <ul role="listbox">
 * @param options.scrollParent the element that actually scrolls
 * @param options.rowHeight    fixed row height in pixels
 * @param options.renderRow    (index) => HTMLElement, called only for visible indices
 */
export function createVirtualList({ container, scrollParent, rowHeight, renderRow, overscan = 6 }) {
    let total = 0;
    let rendered = new Map();
    let frame = 0;

    container.classList.add('is-virtual');

    /**
     * How far the scroll viewport's top edge has passed into the list.
     *
     * Measured from bounding rectangles rather than offsetTop. The obvious formulation,
     * `container.offsetTop - scrollParent.offsetTop`, is only correct when both elements share
     * an offsetParent -- offsetTop is measured from the nearest *positioned* ancestor, so a
     * `position: relative` anywhere between them silently changes the frame of reference and
     * the window lands on the wrong rows. Client rectangles are already in viewport
     * coordinates and already account for scrolling, so their difference is the answer
     * regardless of how anything in between is positioned.
     */
    function offsetIntoList() {
        return scrollParent.getBoundingClientRect().top - container.getBoundingClientRect().top;
    }

    /**
     * How much of the scroll region's top edge is covered by sticky chrome.
     *
     * Read from the scroll parent's own scroll-padding rather than passed in, because that is
     * the declaration the browser already applies to scrollIntoView. Taking the number from
     * the same place means the windowed list and the plain one cannot disagree about where the
     * top of the visible area is -- and a change to the stylesheet does not need a matching
     * change here to stay correct.
     */
    function headroom() {
        const declared = getComputedStyle(scrollParent).scrollPaddingBlockStart;

        // `auto` parses to NaN, which is the correct answer expressed unhelpfully.
        return Number.parseFloat(declared) || 0;
    }

    /**
     * Renders the window for the current scroll position.
     *
     * @param force an index that must be present regardless of scroll, so keyboard
     *              navigation can point aria-activedescendant at a row that exists.
     */
    function render(force = -1) {
        container.style.height = `${total * rowHeight}px`;

        const { first, last } = computeWindow({
            scrollOffset: offsetIntoList(),
            viewportHeight: scrollParent.clientHeight,
            rowHeight,
            total,
            overscan,
        });

        const wanted = new Set();
        for (let i = first; i <= last; i++) wanted.add(i);
        if (force >= 0 && force < total) wanted.add(force);

        // Remove rows that left the window. Done before adding, so peak DOM size stays at
        // one window rather than two.
        for (const [index, node] of rendered) {
            if (!wanted.has(index)) {
                node.remove();
                rendered.delete(index);
            }
        }

        for (const index of wanted) {
            if (rendered.has(index)) continue;

            const row = renderRow(index);

            row.style.position = 'absolute';
            row.style.insetInlineStart = '0';
            row.style.insetInlineEnd = '0';
            row.style.top = `${index * rowHeight}px`;

            // setsize and posinset are what let assistive technology report "5 of 4,312"
            // when only a window is present. Without them a screen reader announces the
            // window size as the list size, which is actively misleading.
            row.setAttribute('aria-setsize', String(total));
            row.setAttribute('aria-posinset', String(index + 1));

            container.append(row);
            rendered.set(index, row);
        }
    }

    function schedule(force = -1) {
        // Coalesced to one render per frame. A scroll event can fire many times per frame,
        // and re-rendering on each is how virtualisation ends up slower than not bothering.
        if (frame) cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            frame = 0;
            render(force);
        });
    }

    const onScroll = () => schedule();

    scrollParent.addEventListener('scroll', onScroll, { passive: true });

    // The window depends on viewport height, so a resize changes it even with no scroll.
    const resizeObserver = new ResizeObserver(() => schedule());
    resizeObserver.observe(scrollParent);

    return {
        /** Replaces the backing count and re-renders from the top. */
        setTotal(next) {
            total = next;

            for (const node of rendered.values()) node.remove();
            rendered = new Map();

            render();
        },

        /** Re-renders the current window, keeping scroll position. */
        refresh(force = -1) {
            for (const node of rendered.values()) node.remove();
            rendered = new Map();

            render(force);
        },

        /** The rendered element for an index, if it is currently in the window. */
        nodeAt(index) {
            return rendered.get(index) ?? null;
        },

        /** Ensures an index is rendered and visible, then returns its element. */
        reveal(index) {
            const top = index * rowHeight;
            const offset = offsetIntoList();

            // The usable top edge is below the sticky header, not at the scroll region's own
            // top. Without this, revealing a row upwards puts it exactly where the header is.
            const viewTop = offset + headroom();
            const viewBottom = offset + scrollParent.clientHeight;

            // Adjusted relatively rather than assigned absolutely, for the same reason
            // offsetIntoList avoids offsetTop: a relative delta needs no assumption about
            // where the list sits inside the scroll region.
            if (top < viewTop) {
                scrollParent.scrollTop -= viewTop - top;
            } else if (top + rowHeight > viewBottom) {
                scrollParent.scrollTop += top + rowHeight - viewBottom;
            }

            // Rendered synchronously and forced to include the index, because the caller is
            // about to point aria-activedescendant at it and that id must already exist.
            render(index);

            return rendered.get(index) ?? null;
        },

        get renderedCount() {
            return rendered.size;
        },

        destroy() {
            if (frame) cancelAnimationFrame(frame);
            scrollParent.removeEventListener('scroll', onScroll);
            resizeObserver.disconnect();
            container.classList.remove('is-virtual');
            container.style.height = '';
        },
    };
}

/**
 * A list that windows itself only when it is long enough to be worth the risk.
 *
 * Every list in this app wants the same three things: render all of it when it is small, window
 * it when it is not, and tear the windowing down again before re-rendering or navigating away.
 * The library did all three inline. The queue and playlists did none of them, so a two-thousand
 * entry playlist rendered two thousand rows -- each a grid with four controls -- which is worse
 * than the case virtualisation was built for in the first place.
 *
 * Written once here rather than a third and fourth time, because the part that is easy to get
 * wrong is not the rendering. It is remembering to destroy the previous renderer: skip it and a
 * detached list keeps a scroll and a resize listener on the shell forever.
 */
export function createAdaptiveList({ container, scrollParent, rowHeight, threshold = VIRTUALISE_ABOVE }) {
    let virtual = null;

    function teardown() {
        virtual?.destroy();
        virtual = null;
    }

    return {
        /**
         * Draws `count` rows, calling `buildRow(index)` for the ones that need to exist.
         *
         * @param {number} count total rows, which above the threshold is far more than are built.
         * @param {(index: number) => HTMLElement} buildRow
         */
        render(count, buildRow) {
            teardown();
            container.replaceChildren();

            if (count <= 0) return;

            if (count > threshold) {
                virtual = createVirtualList({ container, scrollParent, rowHeight, renderRow: buildRow });
                virtual.setTotal(count);
                return;
            }

            // Below the threshold every row is in the DOM. Virtualisation is the riskier path for
            // assistive technology, and most lists never need it.
            const fragment = document.createDocumentFragment();

            for (let index = 0; index < count; index++) fragment.append(buildRow(index));

            container.append(fragment);
        },

        /** Whether the list is currently windowed. */
        get isWindowed() {
            return virtual !== null;
        },

        /**
         * The element for an index, or null if it is not currently rendered.
         *
         * Callers must go through this rather than indexing `container.children`. Under
         * windowing the DOM holds an arbitrary subset in an arbitrary order, so the nth child is
         * not the nth row -- which is how focus ends up on the wrong control after a reorder.
         */
        nodeAt(index) {
            return virtual ? virtual.nodeAt(index) : container.children[index] ?? null;
        },

        /** Scrolls an index into view and returns its element. */
        reveal(index) {
            if (virtual) return virtual.reveal(index);

            const node = container.children[index] ?? null;
            node?.scrollIntoView({ block: 'nearest' });

            return node;
        },

        destroy: teardown,
    };
}
