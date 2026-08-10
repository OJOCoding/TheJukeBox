// Modal prompts, built on <dialog>.
//
// These replace window.prompt and window.confirm, which were the wrong tool twice over.
//
// The fatal problem: prompt() is not universally available. Chrome refuses it in a number of
// contexts and throws "prompt() is not supported" -- which is what made "add to playlist" appear to
// do nothing at all. The call threw, the rejection went unhandled, and the user got no dialog and no
// error. A feature that worked end to end on the server was unusable because of how it asked a
// question.
//
// The secondary problem: a native prompt cannot show a list. Choosing a playlist meant typing the
// number next to its name, which is a worse way to pick from four items than four buttons.
//
// <dialog> is used rather than a hand-rolled overlay because the browser supplies the parts that
// are easy to get wrong: a focus trap, Escape to cancel, inert content behind, and a backdrop.

import { el } from '../core/ui.js';

/**
 * Opens a modal and resolves with whatever `build` passes to `close`.
 *
 * @param {(close: (value: unknown) => void) => {title: string, body: Node[], footer: Node[]}} build
 */
function open(build) {
    return new Promise((resolve) => {
        const dialog = document.createElement('dialog');
        dialog.className = 'dialog';

        let settled = false;

        const close = (value) => {
            if (settled) return;
            settled = true;

            resolve(value);

            // Removed rather than reused: a dialog per question keeps this stateless, and there is
            // never more than one open.
            dialog.close();
            dialog.remove();
        };

        const { title, body, footer } = build(close);

        // Escape and the backdrop both cancel. Without this the promise would never settle and the
        // caller would wait forever for an answer the user has already declined to give.
        dialog.addEventListener('cancel', (event) => {
            event.preventDefault();
            close(null);
        });

        const heading = el('h2', { class: 'dialog__title', text: title });

        dialog.append(
            el('form', {
                class: 'dialog__form',
                method: 'dialog',

                // Submission is handled by the buttons, which know what they mean. Letting the form
                // submit natively would close the dialog with no value.
                onsubmit: (event) => event.preventDefault(),
            },
                heading,
                el('div', { class: 'dialog__body' }, ...body),
                el('div', { class: 'dialog__footer' }, ...footer)),
        );

        document.body.append(dialog);

        dialog.showModal();

        // Focus the first useful control rather than whatever the browser picked, which for a
        // dialog whose first element is a heading is nothing.
        const first = dialog.querySelector('input, button:not([data-secondary])');
        first?.focus();

        if (first instanceof HTMLInputElement) first.select();
    });
}

/**
 * Asks the user to pick one of `items`.
 *
 * @returns the chosen item, the string 'new' if `newLabel` was used, or null if cancelled.
 */
export function chooseFrom({ title, message, items, label, newLabel = null }) {
    return open((close) => ({
        title,
        body: [
            message ? el('p', { class: 'dialog__message', text: message }) : null,

            // A button per item: the whole point of not using a native prompt. Names are set as
            // text, so a playlist called "<img onerror=...>" is a playlist called that.
            el('div', { class: 'dialog__choices' },
                ...items.map((item) => el('button', {
                    class: 'btn',
                    type: 'button',
                    text: label(item),
                    onclick: () => close(item),
                }))),
        ].filter(Boolean),
        footer: [
            newLabel
                ? el('button', {
                    class: 'btn btn--sm',
                    type: 'button',
                    text: newLabel,
                    onclick: () => close('new'),
                })
                : null,
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                'data-secondary': 'true',
                text: 'CANCEL',
                onclick: () => close(null),
            }),
        ].filter(Boolean),
    }));
}

/** Asks for a line of text. Resolves with the trimmed value, or null if cancelled. */
export function askForText({ title, message, value = '', placeholder = '', confirmLabel = 'OK', maxLength = 200 }) {
    return open((close) => {
        const input = el('input', {
            class: 'dialog__input',
            type: 'text',
            value,
            placeholder,
            maxLength,
            autocomplete: 'off',
        });

        const submit = () => {
            const trimmed = input.value.trim();

            // An empty answer is a cancellation in every caller here, so it is treated as one
            // rather than handed back for each of them to check.
            close(trimmed.length === 0 ? null : trimmed);
        };

        // Enter submits, which is the one thing a native prompt got right.
        input.addEventListener('keydown', (event) => {
            if (event.key === 'Enter') {
                event.preventDefault();
                submit();
            }
        });

        return {
            title,
            body: [
                message ? el('p', { class: 'dialog__message', text: message }) : null,
                input,
            ].filter(Boolean),
            footer: [
                el('button', { class: 'btn btn--sm', type: 'button', text: confirmLabel, onclick: submit }),
                el('button', {
                    class: 'btn btn--sm',
                    type: 'button',
                    'data-secondary': 'true',
                    text: 'CANCEL',
                    onclick: () => close(null),
                }),
            ],
        };
    });
}

/**
 * The keyboard shortcuts, as a reference card.
 *
 * These have all worked since B2 and nothing said so. `aria-keyshortcuts` on the play button
 * told assistive technology and nobody else, which is the wrong way round for a feature whose
 * whole value is that you do not have to reach for the mouse.
 *
 * Grouped by what they act on, because "Space pauses" and "Home jumps to the first track" are
 * answers to different questions -- one is about the player, the other about the list you are
 * standing in.
 */
const SHORTCUTS = [
    {
        group: 'PLAYBACK',
        keys: [
            ['Space', 'Play or pause'],
            ['→', 'Next track'],
            ['←', 'Previous track, or restart this one'],
            ['S', 'Shuffle on or off'],
            ['R', 'Cycle repeat: off, all, one'],
            ['M', 'Mute or unmute'],
        ],
    },
    {
        group: 'THE TRACK LIST',
        keys: [
            ['↑ ↓', 'Move the cursor'],
            ['Page Up / Page Down', 'Move by a screenful'],
            ['Home / End', 'First or last track'],
            ['Enter', 'Play the track under the cursor'],
            ['A–Z', 'Jump to the next title starting with what you type'],
        ],
    },
    {
        group: 'EVERYWHERE',
        keys: [
            ['?', 'This list'],
            ['Esc', 'Close a dialog or the account menu'],
            ['Tab', 'Move between controls'],
        ],
    },
];

/**
 * Shows the shortcut reference. Resolves when it is dismissed.
 *
 * Guarded against opening twice: `?` is a keypress, keys repeat when held, and `showModal` on
 * an already-open dialog throws.
 */
let shortcutsOpen = false;

export async function showShortcuts() {
    if (shortcutsOpen) return;

    shortcutsOpen = true;

    try {
        await open((close) => ({
            title: 'KEYBOARD SHORTCUTS',
            body: [
                el('p', {
                    class: 'dialog__message',
                    text: 'Shortcuts are ignored while you are typing in a field or moving '
                        + 'through the track list, so they never eat a keystroke you meant for '
                        + 'something else.',
                }),
                ...SHORTCUTS.map((section) => el('div', { class: 'shortcuts__group' },
                    el('h3', { class: 'shortcuts__heading', text: section.group }),
                    el('dl', { class: 'shortcuts' },
                        ...section.keys.flatMap(([key, description]) => [
                            el('dt', {}, el('kbd', { class: 'kbd', text: key })),
                            el('dd', { text: description }),
                        ])))),
            ],
            footer: [
                el('button', { class: 'btn btn--sm', type: 'button', text: 'CLOSE', onclick: () => close(null) }),
            ],
        }));
    } finally {
        shortcutsOpen = false;
    }
}

/** Asks a yes/no question. Resolves true only on an explicit yes. */
export function askToConfirm({ title, message, confirmLabel = 'CONFIRM' }) {
    return open((close) => ({
        title,
        body: [el('p', { class: 'dialog__message', text: message })],
        footer: [
            el('button', { class: 'btn btn--sm', type: 'button', text: confirmLabel, onclick: () => close(true) }),
            el('button', {
                class: 'btn btn--sm',
                type: 'button',
                'data-secondary': 'true',
                text: 'CANCEL',
                onclick: () => close(false),
            }),
        ],
    }));
}
