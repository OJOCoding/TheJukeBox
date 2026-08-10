// "Add these tracks to a playlist", as one flow.
//
// This lived inside the library view, which is why it was only ever available there: an album
// page could show you twelve tracks and offer no way to keep them together. Extracted rather
// than copied, because the interesting part is not the dialog -- it is the branch where the
// user has no playlists yet and is offered one instead of being sent somewhere else to make it.
//
// Sending them away is the version this replaces: go to another screen, create a playlist, come
// back, find the track again. Four steps to finish the thing they had already asked for.

import { alertUser, notify } from '../core/ui.js';
import { api } from '../core/api.js';
import { askForText, chooseFrom } from './dialog.js';

/**
 * Asks which playlist, then adds.
 *
 * @param {string[]} trackIds  what to add. Ids the server does not agree you own are dropped
 *                             by the endpoint rather than rejected, so a stale page adds what
 *                             it legitimately can.
 * @param {string} label       what to call this in the dialog and the confirmation, e.g. a
 *                             track title or an album name.
 * @returns {Promise<boolean>} whether anything was added.
 */
export async function addTracksToPlaylist(trackIds, label) {
    if (trackIds.length === 0) {
        alertUser('There is nothing here to add.');
        return false;
    }

    let playlists;

    try {
        playlists = await api.playlists();
    } catch (error) {
        alertUser(error.message);
        return false;
    }

    const target = playlists.length === 0
        ? await createPlaylistFor(label)
        : await choosePlaylistFor(label, trackIds.length, playlists);

    if (!target) return false;

    try {
        const result = await api.addToPlaylist(target.id, trackIds);

        if (result.added === 0) {
            alertUser(`Nothing could be added to ${target.name}.`);
            return false;
        }

        // The count is the server's, not ours. It skips ids that are already gone, and saying
        // "12 added" when it accepted nine would be a lie the user could see.
        notify(result.added === trackIds.length
            ? `Added ${describe(label, result.added)} to ${target.name}.`
            : `Added ${result.added} of ${trackIds.length} tracks to ${target.name}.`);

        return true;
    } catch (error) {
        alertUser(error.message);
        return false;
    }
}

/** "\"1 CM\"" for one, "12 tracks from Greatest Hits" for many. */
function describe(label, count) {
    return count === 1 ? `"${label}"` : `${count} tracks from ${label}`;
}

/** Offers to make a playlist, returning it, or null if the user backed out. */
async function createPlaylistFor(label) {
    const name = await askForText({
        title: 'NEW PLAYLIST',
        message: `Name it, and ${label} will go in it.`,
        value: 'Favourites',
        confirmLabel: 'CREATE',
    });

    if (name === null) return null;

    try {
        const created = await api.createPlaylist(name);

        notify(`Created the playlist ${created.name}.`);

        return created;
    } catch (error) {
        alertUser(error.message);
        return null;
    }
}

/**
 * A button per playlist, plus one to make a new one.
 *
 * Buttons rather than "type the number next to the name you want", which is what a native
 * prompt reduced this to. It also cannot throw: prompt() is unsupported in some browser
 * contexts and raises "prompt() is not supported", which is precisely what made this feature
 * appear to do nothing -- the call threw, the rejection went unhandled, and no dialog appeared.
 */
async function choosePlaylistFor(label, count, playlists) {
    const chosen = await chooseFrom({
        title: 'ADD TO PLAYLIST',
        message: `Where should ${describe(label, count)} go?`,
        items: playlists,
        label: (playlist) => `${playlist.name} (${playlist.trackCount})`,
        newLabel: 'NEW PLAYLIST',
    });

    if (chosen === null) return null;

    return chosen === 'new' ? createPlaylistFor(describe(label, count)) : chosen;
}
