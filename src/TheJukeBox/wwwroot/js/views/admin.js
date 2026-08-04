// Account administration.
//
// The operator-facing half of registration control: without a screen here, AdminApproval
// would be a door with no handle -- accounts would queue up as pending and the only way to
// let anyone in would be to edit the database by hand.

import { alertUser, announce, clear, el } from '../core/ui.js';
import { api } from '../core/api.js';

export function renderAdmin(outlet, { store }) {
    const count = el('p', { class: 'view-header__count', text: 'LOADING…' });
    const list = el('div', { class: 'accounts' });

    outlet.append(el(
        'section',
        { class: 'view' },
        el(
            'div',
            { class: 'view-header' },
            el('h1', { text: 'ACCOUNTS' }),
            count,
        ),
        list,
    ));

    const controller = new AbortController();

    void load();

    return () => controller.abort();

    async function load() {
        try {
            const accounts = await api.adminUsers();

            if (controller.signal.aborted) return;

            render(accounts);
        } catch (error) {
            if (controller.signal.aborted) return;

            count.textContent = 'COULD NOT LOAD ACCOUNTS';
            alertUser(error.message);
        }
    }

    function render(accounts) {
        clear(list);

        const pending = accounts.filter((account) => account.status === 'Pending').length;

        count.textContent = pending > 0
            ? `${accounts.length} ACCOUNTS — ${pending} AWAITING APPROVAL`
            : `${accounts.length} ACCOUNTS`;

        if (accounts.length === 0) {
            list.append(el('p', { class: 'view-header__count', text: 'NO ACCOUNTS YET.' }));
            return;
        }

        for (const account of accounts) {
            list.append(row(account));
        }
    }

    function row(account) {
        const singleUser = store.state.session?.registration === 'SingleUser';

        // The identity line is built from textContent, never markup. A display name comes
        // from Google and is attacker-controlled for anyone who can reach the sign-in page,
        // so it reaches the admin screen -- the one screen an operator trusts -- as text.
        const identity = el(
            'div',
            { class: 'accounts__who' },
            el('span', { class: 'accounts__name', text: account.displayName || account.email || account.id }),
            el('span', { class: 'accounts__email', text: account.email ?? '' }),
        );

        const facts = el(
            'div',
            { class: 'accounts__facts' },
            el('span', { class: 'badge', 'data-status': account.status.toLowerCase(), text: account.status.toUpperCase() }),
            account.isAdmin ? el('span', { class: 'badge', text: 'ADMIN' }) : null,
            el('span', {
                class: 'accounts__count',
                text: `${account.trackCount} TRACKS${account.driveConnected ? '' : ' — NO DRIVE GRANT'}`,
            }),
            el('span', { class: 'accounts__count', text: `LAST SEEN ${formatDate(account.lastSeenAtUtc)}` }),
        );

        const actions = el('div', { class: 'accounts__actions' });

        if (account.status !== 'Active') {
            // Suppressed under SingleUser rather than shown-and-rejected: the server refuses
            // it anyway, and an enabled button that always errors is worse than no button.
            if (singleUser && !account.isAdmin) {
                actions.append(el('span', {
                    class: 'accounts__count',
                    text: 'CANNOT APPROVE: SINGLE-USER INSTANCE',
                }));
            } else {
                actions.append(action('APPROVE', 'btn btn-primary', () => api.approveUser(account.id), account));
            }
        }

        // Self-disable is refused by the server too. Not offering it here means the operator
        // does not have to discover that by trying.
        if (account.status !== 'Disabled' && !account.isSelf) {
            actions.append(action('DISABLE', 'btn', () => api.disableUser(account.id), account));
        }

        return el('div', { class: 'accounts__row' }, identity, facts, actions);
    }

    function action(label, className, run, account) {
        return el('button', {
            class: className,
            type: 'button',
            text: label,
            onclick: async (event) => {
                const button = event.currentTarget;

                // Disabled for the duration. Double-clicking APPROVE is easy, and while the
                // endpoint is idempotent, a second call would rotate the security stamp again
                // and log a change that did not happen.
                button.disabled = true;

                try {
                    await run();

                    announce(`${account.displayName || account.email || 'Account'} ${label.toLowerCase()}d.`);

                    // Reloaded rather than patched in place: approving changes the pending
                    // count in the header and, under some modes, which buttons the other rows
                    // should offer.
                    await load();
                } catch (error) {
                    button.disabled = false;
                    alertUser(error.message);
                }
            },
        });
    }
}

/**
 * A date the operator can read, in their own locale.
 *
 * Falls back to the raw value rather than throwing: a malformed timestamp is a diagnostic
 * detail, not a reason for the whole screen to fail.
 */
function formatDate(value) {
    const parsed = new Date(value);

    if (Number.isNaN(parsed.getTime())) return String(value ?? 'NEVER');

    return parsed.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}
