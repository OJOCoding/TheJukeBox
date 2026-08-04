// The single place this app talks to its server.
//
// Centralised so that "your session expired" is handled once. The previous code called
// fetch inline and checked response.ok, which cannot distinguish an expired session from
// a genuine failure -- and because the server used to answer 400 for everything
// including auth problems, no caller could have told them apart anyway.

/** A failed request, carrying the server's stable problem type. */
export class ApiError extends Error {
    constructor(status, problem) {
        super(problem?.detail || problem?.title || `Request failed with ${status}`);
        this.name = 'ApiError';
        this.status = status;

        // The urn:jukebox:* value. This is the contract the UI switches on; the title
        // and detail are human strings and may change freely.
        this.type = problem?.type ?? null;
        this.problem = problem ?? null;
    }

    /** True when the user is signed in but has no usable Drive grant. */
    get needsDriveConsent() {
        return this.type === 'urn:jukebox:drive-consent-required';
    }

    get isUnauthenticated() {
        return this.status === 401;
    }
}

let onUnauthenticated = () => {};

/** Registers what to do when the server reports the session is gone. */
export function handleUnauthenticatedWith(callback) {
    onUnauthenticated = callback;
}

async function request(path, { method = 'GET', body = null, signal = null } = {}) {
    const headers = {
        // Explicit, so the server's cookie handler answers 401 rather than redirecting
        // to a login page. A fetch handed an HTML document where it expected JSON is the
        // single most confusing failure mode in a cookie-authenticated SPA.
        Accept: 'application/json',
    };

    if (body !== null) headers['Content-Type'] = 'application/json';

    const response = await fetch(path, {
        method,
        headers,
        body: body === null ? null : JSON.stringify(body),

        // Same-origin cookies carry the session. Never 'include': there is no
        // cross-origin case, and the server sends no CORS headers precisely so that
        // one cannot exist.
        credentials: 'same-origin',
        signal,
    });

    if (response.status === 401) {
        onUnauthenticated();
        throw new ApiError(401, await problemOf(response));
    }

    if (!response.ok) {
        throw new ApiError(response.status, await problemOf(response));
    }

    if (response.status === 204) return null;

    const contentType = response.headers.get('content-type') ?? '';
    return contentType.includes('json') ? response.json() : null;
}

async function problemOf(response) {
    try {
        const contentType = response.headers.get('content-type') ?? '';
        return contentType.includes('json') ? await response.json() : null;
    } catch {
        // A body that is not readable or not JSON must not mask the status code.
        return null;
    }
}

/**
 * Whether this session may use the library.
 *
 * `authenticated` is not enough. Anyone with a Google account can complete sign-in, so the
 * session is authenticated the moment the account exists -- but on an instance that requires
 * approval, every library call still answers 403 until an administrator approves it. Treating
 * authenticated as admitted would render the library shell and then fill it with errors.
 *
 * Defined here rather than inline at each call site because it is checked in four places
 * (chrome, router, boot, welcome) and they must agree; three of them agreeing is a bug that
 * only shows up for one kind of user.
 */
export function isAdmitted(session) {
    return session?.authenticated === true && session?.status === 'Active';
}

/** Admitted, and Drive is connected: the state in which the app is fully usable. */
export function isReady(session) {
    return isAdmitted(session) && session?.driveConnected === true;
}

export const api = {
    /** The bootstrap call. Always 200, so the shell can distinguish signed-out from broken. */
    session: () => request('/api/session'),

    tracks: ({ q = '', sort = 'title', limit = 500, offset = 0, signal = null } = {}) => {
        const query = new URLSearchParams({ sort, limit: String(limit), offset: String(offset) });
        if (q) query.set('q', q);

        return request(`/api/tracks?${query}`, { signal });
    },

    syncStatus: () => request('/api/sync/status'),

    requestSync: () => request('/api/sync', { method: 'POST' }),

    signOut: () => request('/api/auth/logout', { method: 'POST' }),

    adminUsers: () => request('/api/admin/users'),

    approveUser: (id) => request(`/api/admin/users/${encodeURIComponent(id)}/approve`, { method: 'POST' }),

    disableUser: (id) => request(`/api/admin/users/${encodeURIComponent(id)}/disable`, { method: 'POST' }),

    /**
     * The audio URL for a track.
     *
     * Not fetched here. It is assigned to an <audio> element's src, so the browser
     * issues the ranged requests itself and the server's proxy answers them.
     */
    streamUrl: (trackId) => `/api/tracks/${encodeURIComponent(trackId)}/stream`,
};
