// OS-level media integration.
//
// Puts the current track on the lock screen, in the notification shade, and on the media
// widget in Windows and macOS, and makes hardware media keys and headphone buttons work.
// Cheap to add and disproportionately large in how finished the app feels.

/**
 * Binds the Media Session API to the player.
 *
 * @param player  the playback controller
 * @param store   the app store, subscribed to for track and playback state
 * @param audio   the audio element, for fastSeek and position reporting
 */
export function bindMediaSession({ player, store, audio }) {
    // Absent on Firefox desktop and older Safari. Nothing here is load-bearing, so the
    // whole feature is skipped rather than shimmed.
    if (!('mediaSession' in navigator)) return () => {};

    const session = navigator.mediaSession;

    let lastTrackId = null;
    let lastPositionReport = 0;

    for (const [action, handler] of Object.entries({
        play: () => player.toggle(),
        pause: () => player.toggle(),
        stop: () => player.toggle(),
        previoustrack: () => player.previous(),
        nexttrack: () => player.next(),
        seekbackward: (details) => player.seekTo(audio.currentTime - (details.seekOffset || 10)),
        seekforward: (details) => player.seekTo(audio.currentTime + (details.seekOffset || 10)),
        seekto: (details) => {
            // fastSeek trades accuracy for speed by jumping to the nearest keyframe, which
            // is the right trade when the user is dragging an OS scrubber.
            if (details.fastSeek && typeof audio.fastSeek === 'function') audio.fastSeek(details.seekTime);
            else player.seekTo(details.seekTime);
        },
    })) {
        try {
            session.setActionHandler(action, handler);
        } catch {
            // Unsupported actions throw rather than no-op. Not knowing which platform
            // supports which is fine; attempting each and ignoring refusals is simpler and
            // more durable than feature-detecting a moving target.
        }
    }

    const unsubscribe = store.subscribe((state) => {
        session.playbackState = state.playing ? 'playing' : 'paused';

        if (state.currentTrack && state.currentTrackId !== lastTrackId) {
            lastTrackId = state.currentTrackId;
            setMetadata(session, state.currentTrack);
        }

        if (!state.currentTrack) {
            lastTrackId = null;
            session.metadata = null;
        }

        // Greying out rather than lying: an unset handler makes the OS disable the button,
        // whereas a handler that does nothing leaves an enabled control that ignores presses.
        setAvailability(session, 'nexttrack', state.hasNext, () => player.next());
        setAvailability(session, 'previoustrack', state.hasPrevious, () => player.previous());

        reportPosition(session, audio, state);
    });

    return () => {
        unsubscribe();
        session.metadata = null;
    };

    function reportPosition(target, element, state) {
        if (!('setPositionState' in target)) return;
        if (!Number.isFinite(element.duration) || element.duration <= 0) return;

        // Throttled to about 1 Hz. Updating on every timeupdate makes the position jitter
        // visibly in some OS scrubbers.
        const now = performance.now();
        if (now - lastPositionReport < 1000) return;
        lastPositionReport = now;

        try {
            target.setPositionState({
                duration: element.duration,
                playbackRate: element.playbackRate || 1,

                // Clamped: setPositionState throws when position exceeds duration, which
                // happens transiently at a track boundary.
                position: Math.min(Math.max(state.position || 0, 0), element.duration),
            });
        } catch {
            // Ignored. A rejected position report is cosmetic.
        }
    }
}

function setMetadata(session, track) {
    session.metadata = new window.MediaMetadata({
        title: track.title || 'Unknown',
        artist: track.artist || '',
        album: track.album || '',
        artwork: artworkFor(track),
    });
}

function artworkFor(track) {
    // Real cover art when the file had an embedded picture, and the app icon otherwise, so
    // the lock screen never shows a blank tile.
    if (track.artSha256) {
        return [
            { src: `/api/art/${track.artSha256}/96`, sizes: '96x96', type: 'image/jpeg' },
            { src: `/api/art/${track.artSha256}/640`, sizes: '640x640', type: 'image/jpeg' },
        ];
    }

    return [
        { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
        { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
    ];
}

function setAvailability(session, action, available, handler) {
    try {
        session.setActionHandler(action, available ? handler : null);
    } catch {
        // As above: some platforms do not implement every action.
    }
}
