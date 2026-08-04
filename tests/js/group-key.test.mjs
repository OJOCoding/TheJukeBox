// The artist key encoder, and its contract with the server.
//
//   node --test tests/js/group-key.test.mjs
//
// The album detail view links back to the artist, and it builds that key on the client because the
// album response already carries the artist name. That makes the encoding a contract implemented
// twice, in two languages -- exactly the kind of duplication that drifts silently: a mismatch is
// not an error, it is a 404 on one link.
//
// The expected values here are the server's own output, checked in as fixtures. GroupKeyContractTests
// on the .NET side asserts the same strings, so if either implementation changes, one of the two
// suites fails.

import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { encodeArtistKey } from '../../src/TheJukeBox/wwwroot/js/views/browse.js';

/**
 * Fixtures shared with the .NET suite.
 *
 * Base64url of "a" + the name, UTF-8, unpadded. The leading "a" is the artist-kind marker; it is
 * what makes the key for an absent name non-empty, and an empty key is an unreachable URL.
 */
const EXPECTED = {
    'Radiohead': 'YVJhZGlvaGVhZA',
    'AC/DC': 'YUFDL0RD',
    'Sigur Rós': 'YVNpZ3VyIFLDs3M',
    '': 'YQ',
    '100% Silk': 'YTEwMCUgU2lsaw',
    '日本語': 'YeaXpeacrOiqng',
};

describe('encodeArtistKey', () => {
    it('produces the same keys as the server', () => {
        for (const [artist, expected] of Object.entries(EXPECTED)) {
            assert.equal(encodeArtistKey(artist), expected, `artist "${artist}"`);
        }
    });

    /** An absent name must still yield a usable URL segment. */
    it('never returns an empty key', () => {
        assert.notEqual(encodeArtistKey(''), '');
        assert.notEqual(encodeArtistKey(null), '');
        assert.notEqual(encodeArtistKey(undefined), '');
    });

    it('treats null and undefined as an absent name', () => {
        assert.equal(encodeArtistKey(null), encodeArtistKey(''));
        assert.equal(encodeArtistKey(undefined), encodeArtistKey(''));
    });

    /**
     * btoa throws on any code point above U+00FF, so the UTF-8 bytes have to be mapped to a byte
     * string first. Artist names routinely contain such characters, so this is the difference
     * between working and throwing for a large fraction of real libraries.
     */
    it('handles names outside Latin-1', () => {
        for (const name of ['日本語のアーティスト', 'Björk', 'Сергей', 'emoji 🎵']) {
            const key = encodeArtistKey(name);

            assert.ok(key.length > 0, `"${name}" produced no key`);
            assert.equal(key, encodeURIComponent(key), `"${name}" produced a key needing escaping`);
        }
    });

    it('produces url-safe keys with no padding', () => {
        for (const name of Object.keys(EXPECTED)) {
            const key = encodeArtistKey(name);

            assert.ok(!key.includes('='), `"${name}" produced padding`);
            assert.ok(!key.includes('+'), `"${name}" produced a plus`);
            assert.ok(!key.includes('/'), `"${name}" produced a slash`);
        }
    });

    it('is stable across calls', () => {
        assert.equal(encodeArtistKey('Boards of Canada'), encodeArtistKey('Boards of Canada'));
    });

    it('distinguishes different artists', () => {
        assert.notEqual(encodeArtistKey('Queen'), encodeArtistKey('ABBA'));
    });
});
