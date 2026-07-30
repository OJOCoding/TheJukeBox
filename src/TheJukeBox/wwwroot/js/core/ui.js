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
    requestAnimationFrame(() => { region.textContent = message; });
}

/** Announces assertively. For errors only; it interrupts whatever is being read. */
export function alertUser(message) {
    const region = alerts();
    if (!region) return;

    region.textContent = '';
    requestAnimationFrame(() => { region.textContent = message; });
}
