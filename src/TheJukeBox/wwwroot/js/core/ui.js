// Small DOM, formatting and announcement helpers.
//
// Nothing here builds markup from strings. The previous player assembled rows with
// innerHTML and string concatenation, which meant escaping had to be remembered at
// every call site -- and it was missed in the error path, where showError injected an
// unescaped message. Setting textContent cannot inject, so the question never arises.

/** Creates an element, assigning properties and appending children. */
export function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);

    for (const [key, value] of Object.entries(props)) {
        if (value === null || value === undefined) continue;

        if (key === 'class') node.className = value;
        else if (key === 'text') node.textContent = value;
        else if (key.startsWith('data-') || key.startsWith('aria-')) node.setAttribute(key, value);
        else node[key] = value;
    }

    for (const child of children.flat()) {
        if (child === null || child === undefined) continue;
        node.append(child);
    }

    return node;
}

/** Empties a node. */
export function clear(node) {
    node.replaceChildren();
}

/** Clones a <template> by id and returns its first element child. */
export function fromTemplate(id) {
    const template = document.getElementById(id);
    if (!template) throw new Error(`Missing template: ${id}`);
    return template.content.firstElementChild.cloneNode(true);
}

/** "3:42", or "1:03:20" past an hour. Null and nonsense render as "--:--". */
export function mmss(totalSeconds) {
    if (!Number.isFinite(totalSeconds) || totalSeconds < 0) return '--:--';

    const t = Math.floor(totalSeconds);
    const hours = Math.floor(t / 3600);
    const minutes = Math.floor((t % 3600) / 60);
    const seconds = t % 60;
    const pad = (n) => String(n).padStart(2, '0');

    return hours ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

/**
 * "3 minutes 42 seconds", for aria-valuetext.
 *
 * A raw aria-valuenow of 222 is announced as "two hundred twenty two", which tells a
 * screen-reader user nothing useful about a position in a track.
 */
export function spoken(totalSeconds) {
    if (!Number.isFinite(totalSeconds) || totalSeconds < 0) return 'unknown';

    const t = Math.floor(totalSeconds);
    const minutes = Math.floor(t / 60);
    const seconds = t % 60;
    const parts = [];

    if (minutes) parts.push(`${minutes} minute${minutes === 1 ? '' : 's'}`);
    if (seconds || !minutes) parts.push(`${seconds} second${seconds === 1 ? '' : 's'}`);

    return parts.join(' ');
}

/** An ISO 8601 duration, for the datetime attribute on <time>. */
export function isoDuration(totalSeconds) {
    if (!Number.isFinite(totalSeconds) || totalSeconds < 0) return null;

    const t = Math.floor(totalSeconds);
    return `PT${Math.floor(t / 60)}M${t % 60}S`;
}

const announcer = () => document.getElementById('announcer');
const alerts = () => document.getElementById('alerts');

/**
 * Announces politely: waits for a pause rather than interrupting.
 *
 * Cleared first and set on the next frame, because assigning the same text twice in a
 * row is not a change and would not be announced at all.
 */
export function announce(message) {
    const region = announcer();
    if (!region) return;

    region.textContent = '';

    // setTimeout, not requestAnimationFrame. Both give the region a tick to notice the change,
    // but rAF is paused in a backgrounded tab -- which for a music player is a normal state, and
    // the moment a track change most needs announcing.
    setTimeout(() => { region.textContent = message; }, 0);
}

/**
 * Confirms something the user did: politely announced, and visibly shown.
 *
 * `announce` alone is the right call for narration nobody asked for -- a route change, sync
 * progress. It is the wrong call for the result of a button press, because the only feedback for
 * "added to playlist" was then a message in a clipped one-pixel region, which is indistinguishable
 * from the button having done nothing.
 */
export function notify(message) {
    announce(message);
    showToast(message);
}

/**
 * Reports a problem: assertively to assistive technology, and visibly to everyone else.
 *
 * The visible half was missing, and its absence was the single most misleading thing in the app.
 * The live region is `sr-only` -- one clipped pixel -- so every error the app produced was
 * announced to a screen reader and shown to nobody. "Select a track first", "Create a playlist
 * first", "Could not load the library": all of it went into a void, and the app looked like it
 * silently ignored the click. Features that worked correctly were reported as broken because the
 * only thing that would have explained them was invisible.
 *
 * The live region is still the accessibility mechanism; the toast is purely visual and marked
 * aria-hidden, so the message is announced once rather than twice.
 */
export function alertUser(message) {
    const region = alerts();

    if (region) {
        region.textContent = '';
        setTimeout(() => { region.textContent = message; }, 0);
    }

    showToast(message);
}

/** How long a toast stays before fading, in milliseconds. */
const TOAST_MS = 6000;

let toastTimer = null;

/**
 * Shows the visible half of an alert.
 *
 * Appended to a container created on demand rather than declared in index.html, so nothing has to
 * be added to the startup dom-map contract for a purely decorative element. Messages are text, set
 * with textContent: an error can carry a server-supplied string, and this app renders no markup
 * from data anywhere.
 */
function showToast(message) {
    let host = document.getElementById('toasts');

    if (!host) {
        host = document.createElement('div');
        host.id = 'toasts';
        host.className = 'toasts';

        // Hidden from assistive technology: the #alerts live region already announced this, and
        // duplicating it here would read every error twice.
        host.setAttribute('aria-hidden', 'true');

        document.body.append(host);
    }

    const toast = document.createElement('div');
    toast.className = 'toast';
    toast.textContent = message;

    const dismiss = document.createElement('button');
    dismiss.type = 'button';
    dismiss.className = 'toast__close';
    dismiss.textContent = '✕';

    // Dismissible, because a long message that covers the control you were about to press is
    // worse than no message. Also tabbable-free: aria-hidden means a keyboard user cannot reach
    // it, so it must never be the only way to proceed -- it is not, it only hides text.
    dismiss.addEventListener('click', () => toast.remove());

    toast.append(dismiss);
    host.append(toast);

    // Only the most recent few are kept. A failing background poll could otherwise stack toasts
    // until they covered the page.
    while (host.children.length > 3) host.firstElementChild?.remove();

    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => host.replaceChildren(), TOAST_MS);
}
