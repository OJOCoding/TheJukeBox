// A minimal observable store.
//
// The previous player kept seven module-level mutable globals (currentPlaylist,
// currentTrackIndex, isShuffle, shuffledPlaylist, hasUserSelectedTrack, ...) that any
// function could write at any time. That is how the shuffle state and the shuffled
// order came to disagree: two variables encoded one fact, and only some code paths
// updated both.
//
// One state object with one write path means every change is observable, and rendering
// is a function of state rather than a side effect scattered across handlers.

/** Creates a store holding `initial`. */
export function createStore(initial) {
    let state = Object.freeze({ ...initial });
    const listeners = new Set();

    return {
        get state() {
            return state;
        },

        /** Shallow-merges a patch and notifies subscribers. */
        set(patch) {
            const next = Object.freeze({ ...state, ...patch });

            // Reference equality on every key means no-op writes do not cause a render.
            // This matters because the position ticker fires many times a second.
            const changed = Object.keys(patch).some((key) => state[key] !== next[key]);
            if (!changed) return state;

            state = next;

            for (const listener of listeners) {
                listener(state);
            }

            return state;
        },

        /** Subscribes to changes. Returns an unsubscribe function. */
        subscribe(listener) {
            listeners.add(listener);
            return () => listeners.delete(listener);
        },
    };
}
